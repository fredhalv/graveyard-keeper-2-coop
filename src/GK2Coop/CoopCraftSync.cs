using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace GK2Coop
{
    /// <summary>
    /// Makes crafting stations shared: the workbenches, anvils, ovens and kilns that open the
    /// game's craft window (<c>WGODef.InteractionType.Craft</c>).
    ///
    /// Before this, a joiner's craft existed on the joiner's machine only: queued there, advanced
    /// there, finished there. The host's station stayed idle (measured in
    /// <c>artifacts/craft-experiment-0.26.0</c>). Replicating the button press would not have
    /// fixed it: a craft takes its ingredients from the station, from every chest in the zone and
    /// from the local player, so two machines each running the craft would each consume.
    ///
    /// So the host is the only machine that runs these stations:
    /// <list type="bullet">
    /// <item>A joiner never changes such a station's queue itself. Adding, removing and cancelling
    /// become requests to the host, sent after a fresh upload of the joiner's inventory.</item>
    /// <item>The host queues the craft and remembers who ordered it. When the craft starts, the
    /// ingredients are taken as that player — their inventory, standing at that station — and the
    /// player is told exactly what to remove on their side.</item>
    /// <item>A joiner does not advance these stations; once a second the host sends every station
    /// whose state changed, serialized by the game's own <c>LazySerializer</c>, and the joiner
    /// loads it into its copy.</item>
    /// </list>
    /// Out of scope for now: instant crafts (<c>skipQueue</c>), gardens, graves, town repairs and
    /// other interaction types, which keep their previous behaviour.
    /// </summary>
    internal static class CoopCraftSync
    {
        internal const string RequestMessage = "GK2Coop.CraftRequest.v1";
        internal const string StateMessage = "GK2Coop.CraftState.v1";
        internal const string InventoryMessage = "GK2Coop.CraftInventory.v1";
        internal const string TakeMessage = "GK2Coop.CraftInventoryTake.v1";
        internal const string TakeResultMessage = "GK2Coop.CraftInventoryTakeResult.v1";
        internal const string ConsumeMessage = "GK2Coop.CraftConsume.v1";

        private enum Op
        {
            Add = 0,
            Remove = 1,
            Cancel = 2,
            Work = 3,
            SetCount = 4
        }

        private sealed class Owner
        {
            internal ulong ClientId;
        }

        private static ManualLogSource log;
        private static bool applying;
        private static bool reentrant;
        private static float nextBroadcast;
        private static float nextWorkUpload;
        private static readonly Dictionary<string, byte[]> lastSent = new Dictionary<string, byte[]>();
        private static readonly Dictionary<string, byte[]> lastInventorySent = new Dictionary<string, byte[]>();
        private static readonly ConditionalWeakTable<object, Owner> owners = new ConditionalWeakTable<object, Owner>();
        private static MethodInfo removeRequirements;
        private static Owner startingOwner;
        private static bool localPlayerWorking;
        private static int ingredientLogsLeft = 20;
        private static int requestsSent;
        private static int requestsApplied;
        private static int statesApplied;
        private static int consumedForClients;

        internal static bool Enabled { get; set; } = true;

        internal static string Describe()
        {
            return $"craft sync: requests sent={requestsSent}, applied={requestsApplied}, states applied={statesApplied}, remote ingredient takes={consumedForClients}";
        }

        internal static void Init(ManualLogSource source)
        {
            log = source;
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type component = Plugin.FindGameType("CraftComponent");
                Type elementBase = Plugin.FindGameType("CraftElementBase");
                if (component == null || elementBase == null)
                {
                    log.LogWarning("Craft sync: CraftComponent not found; stations stay local.");
                    Enabled = false;
                    return;
                }
                Patch(harmony, AccessTools.Method(component, "AddToQueue"), nameof(AddToQueuePrefix));
                Patch(harmony, AccessTools.Method(component, "RemoveFromQueue"), nameof(RemoveFromQueuePrefix));
                Patch(harmony, AccessTools.Method(component, "Cancel"), nameof(CancelPrefix));
                Patch(harmony, AccessTools.Method(component, "Update", new[] { typeof(float) }), nameof(SimulationPrefix));
                Patch(harmony, AccessTools.Method(component, "UpdateManual", new[] { typeof(int) }), nameof(UpdateManualPrefix));
                Patch(harmony, AccessTools.Method(component, "TryContinueFromQueue"), nameof(TryContinuePrefix));
                var countChanged = new HarmonyMethod(typeof(CoopCraftSync).GetMethod(nameof(CountChangedPostfix), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(AccessTools.PropertySetter(elementBase, "Count"), postfix: countChanged);
                harmony.Patch(AccessTools.PropertySetter(elementBase, "IsInfinite"), postfix: countChanged);
                MethodInfo take = Plugin.FindGameType("Inventory").GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "RemoveItemById" && m.GetParameters().Length == 5);
                if (take == null) throw new MissingMethodException("Inventory", "RemoveItemById");
                harmony.Patch(take, postfix: new HarmonyMethod(typeof(CoopCraftSync).GetMethod(nameof(InventoryTakePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                // Start clones the queued element and takes ingredients for the clone, so the owner is
                // carried across the call rather than looked up on the clone.
                harmony.Patch(AccessTools.Method(component, "Start", new[] { elementBase }),
                    prefix: new HarmonyMethod(typeof(CoopCraftSync).GetMethod(nameof(StartPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(CoopCraftSync).GetMethod(nameof(StartFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                MethodInfo playerUseTool = AccessTools.Method(Plugin.FindGameType("PlayerCraftActivity"), "UseTool");
                if (playerUseTool != null)
                {
                    harmony.Patch(playerUseTool,
                        prefix: new HarmonyMethod(typeof(CoopCraftSync).GetMethod(nameof(PlayerWorkPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(CoopCraftSync).GetMethod(nameof(PlayerWorkFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                removeRequirements = AccessTools.Method(component, "RemoveRequirements", new[] { elementBase });
                Patch(harmony, removeRequirements, nameof(RemoveRequirementsPrefix));
                log.LogInfo("Craft sync: crafting stations are run by the host and mirrored to joiners.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                log.LogWarning("Craft sync disabled: " + ex.Message);
            }
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefix)
        {
            if (target == null)
            {
                throw new MissingMethodException("CraftComponent", prefix);
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CoopCraftSync).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // ------------------------------------------------------------------ which stations

        private static bool IsClient()
        {
            NetworkManager netcode = NetworkManager.Singleton;
            return netcode != null && netcode.IsConnectedClient && !netcode.IsHost;
        }

        private static bool IsHostWithClients()
        {
            NetworkManager netcode = NetworkManager.Singleton;
            return netcode != null && netcode.IsHost && netcode.IsListening && netcode.ConnectedClientsIds.Count > 1;
        }

        private sealed class Classification
        {
            internal bool Shared;
            internal float Until;
        }

        private static readonly ConditionalWeakTable<object, Classification> classified = new ConditionalWeakTable<object, Classification>();

        /// <summary>
        /// A station that opens the craft window and is not being dismantled right now. Asked on
        /// every craft update on a joiner, so the answer is kept for a few seconds per station.
        /// </summary>
        private static bool IsSharedStation(object component, out object wgoData)
        {
            wgoData = CoopDiagnostics.GetMember(component, "CraftableObject");
            Classification cached = classified.GetOrCreateValue(component);
            if (Time.unscaledTime < cached.Until)
            {
                return cached.Shared;
            }
            cached.Shared = Classify(component, wgoData);
            cached.Until = Time.unscaledTime + 3f;
            return cached.Shared;
        }

        private static bool Classify(object component, object wgoData)
        {
            if (wgoData == null || !Plugin.FindGameType("WgoData").IsInstanceOfType(wgoData))
            {
                return false;
            }
            object definition = CoopDiagnostics.GetMember(wgoData, "Definition");
            if (definition == null || Convert.ToString(CoopDiagnostics.GetMember(definition, "interactionType")) != "Craft")
            {
                return false;
            }
            if (!Convert.ToBoolean(CoopDiagnostics.GetMember(component, "HasCraftsByBalance")))
            {
                return false;
            }
            // A recipe that replaces or removes the object when it finishes (repairs, blockages,
            // quest doors) would do so only on the host: object replacement is not replicated yet,
            // so on a joiner the object would stay as it was, stuck mid-craft. Those keep running
            // on each machine as before.
            if (CoopDiagnostics.GetMember(component, "AvailableCrafts") is System.Collections.IEnumerable recipes)
            {
                foreach (object recipe in recipes)
                {
                    // Production only: every recipe makes items. A recipe that makes nothing (a
                    // repair, a quest door, a blockage) changes the world through scripts run when
                    // it finishes, and those would run on the host alone.
                    object outputs = recipe == null ? null : CoopDiagnostics.GetMember(recipe, "outputItems");
                    if (recipe == null || recipe.GetType().Name != "CraftDef" ||
                        !string.IsNullOrEmpty(Convert.ToString(CoopDiagnostics.GetMember(recipe, "replaceWgoId"))) ||
                        Convert.ToBoolean(CoopDiagnostics.GetMember(recipe, "isObjDestroyCraft")) ||
                        outputs == null || !Convert.ToBoolean(CoopDiagnostics.GetMember(outputs, "HasOutputItems")))
                    {
                        return false;
                    }
                }
            }
            object current = CoopDiagnostics.GetMember(component, "CurrentCraftElement");
            object currentDef = current == null ? null : CoopDiagnostics.GetMember(current, "Def");
            return currentDef == null || !Convert.ToBoolean(CoopDiagnostics.GetMember(currentDef, "isObjDestroyCraft"));
        }

        /// <summary>For diagnostics and tests: whether the host runs this station for everyone.</summary>
        internal static bool IsShared(object component)
        {
            return Enabled && component != null && IsSharedStation(component, out _);
        }

        private static bool JoinerMustForward(object component, out object wgoData)
        {
            wgoData = null;
            return Enabled && !applying && IsClient() && IsSharedStation(component, out wgoData);
        }

        // ------------------------------------------------------------------ joiner side

        /// <summary>
        /// Dismantling (and cancelling it) runs on the dismantling player's machine, as a station
        /// being dismantled is not shared; craft-end sync tells the others when it is gone. Asked
        /// before the destroy craft is current, the station still looked shared, so the joiner sent
        /// it to the host instead: the host then unshared the station and turned down every later
        /// request for it, and the joiner's copy never went away.
        /// </summary>
        private static bool IsDestroyCraft(object component, object craftElement)
        {
            object def = craftElement == null ? null : CoopDiagnostics.GetMember(craftElement, "Def");
            if (def == null || !Convert.ToBoolean(CoopDiagnostics.GetMember(def, "isObjDestroyCraft")))
            {
                return false;
            }
            classified.Remove(component);
            return true;
        }

        private static bool AddToQueuePrefix(object __instance, object craftElement, bool addToQueueTop, int queueIdx, ref object __result)
        {
            if (IsDestroyCraft(__instance, craftElement) || !JoinerMustForward(__instance, out object wgo))
            {
                return true;
            }
            string craftId = Convert.ToString(CoopDiagnostics.GetMember(craftElement, "CraftId"));
            int count = Convert.ToInt32(CoopDiagnostics.GetMember(craftElement, "Count"));
            SendRequest(Op.Add, wgo, craftId, count, addToQueueTop, queueIdx);
            __result = craftElement;
            return false;
        }

        private static bool RemoveFromQueuePrefix(object __instance, object craftElement, bool removeEvenIfStarted)
        {
            if (IsDestroyCraft(__instance, craftElement) || !JoinerMustForward(__instance, out object wgo))
            {
                return true;
            }
            int index = QueueIndex(__instance, craftElement);
            SendRequest(Op.Remove, wgo, Convert.ToString(CoopDiagnostics.GetMember(craftElement, "CraftId")), index, removeEvenIfStarted, -1);
            return false;
        }

        private static bool CancelPrefix(object __instance)
        {
            if (!JoinerMustForward(__instance, out object wgo))
            {
                return true;
            }
            SendRequest(Op.Cancel, wgo, string.Empty, 0, false, -1);
            return false;
        }

        private static readonly Dictionary<object, KeyValuePair<WgoData, int>> queueIndex = new Dictionary<object, KeyValuePair<WgoData, int>>();
        private static float queueIndexBuilt = -10f;

        /// <summary>
        /// The craft windows change a queued craft's count in place (<c>Count++</c>, <c>Count--</c>,
        /// the furnace's plus and minus, the infinite switch), not through a queue call. On a joiner
        /// that changed its own copy only: the host kept the old count, and the two queues went
        /// apart (Workshop report, 5 October 2026: a Furnace II queue messed up). The new count goes
        /// to the host, which runs the station.
        /// </summary>
        private static void CountChangedPostfix(CraftElementBase __instance)
        {
            if (!Enabled || applying || __instance == null || !IsClient())
            {
                return;
            }
            try
            {
                if (!queueIndex.TryGetValue(__instance, out KeyValuePair<WgoData, int> place) && Time.unscaledTime - queueIndexBuilt > 0.5f)
                {
                    BuildQueueIndex();
                    queueIndex.TryGetValue(__instance, out place);
                }
                if (place.Key == null)
                {
                    return;
                }
                SendRequest(Op.SetCount, place.Key, __instance.CraftId, __instance.Count, __instance.IsInfinite, place.Value);
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not send a changed craft count: " + Inner(ex).Message);
            }
        }

        private static void BuildQueueIndex()
        {
            queueIndex.Clear();
            queueIndexBuilt = Time.unscaledTime;
            MainGame mainGame = MainGame.Instance;
            List<GameSceneData> scenes = mainGame?.GameSave?.worldData?.gameSceneDataList;
            if (scenes == null)
            {
                return;
            }
            foreach (GameSceneData scene in scenes)
            {
                if (scene?.wgoDataList == null)
                {
                    continue;
                }
                foreach (WgoData wgo in scene.wgoDataList)
                {
                    CraftComponent component = wgo?.CraftComponent;
                    if (component == null || component.CraftElementsQueue == null || component.CraftElementsQueue.Count == 0 || !IsSharedStation(component, out _))
                    {
                        continue;
                    }
                    for (int i = 0; i < component.CraftElementsQueue.Count; i++)
                    {
                        if (component.CraftElementsQueue[i] != null)
                        {
                            queueIndex[component.CraftElementsQueue[i]] = new KeyValuePair<WgoData, int>(wgo, i);
                        }
                    }
                }
            }
        }

        /// <summary>The host runs the station; the joiner shows what the host sends.</summary>
        private static bool SimulationPrefix(object __instance)
        {
            return applying || !(Enabled && IsClient() && IsSharedStation(__instance, out _));
        }

        /// <summary>
        /// A joiner working a station with a tool: the tick goes to the host, which runs the
        /// station. Without this a joiner's hammering advanced nothing anywhere — its own copy is
        /// not simulated, and tool-use replication is a separate, still-experimental option.
        /// </summary>
        private static bool UpdateManualPrefix(object __instance, int deltaTicks)
        {
            if (!JoinerMustForward(__instance, out object wgo))
            {
                return true;
            }
            // Only the local player's own tool use is this player's work. Zombies working the
            // station on this machine are a copy of the host's zombies, which already work it
            // there; forwarding their ticks would run the station twice as fast.
            if (deltaTicks > 0 && localPlayerWorking)
            {
                SendRequest(Op.Work, wgo, string.Empty, deltaTicks, false, -1);
            }
            return false;
        }

        private static void PlayerWorkPrefix()
        {
            localPlayerWorking = true;
        }

        private static Exception PlayerWorkFinalizer(Exception __exception)
        {
            localPlayerWorking = false;
            return __exception;
        }

        private static bool TryContinuePrefix(object __instance, ref bool __result)
        {
            if (applying || !(Enabled && IsClient() && IsSharedStation(__instance, out _)))
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static void SendRequest(Op op, object wgo, string craftId, int count, bool flag, int queueIdx, string itemId = "")
        {
            NetworkManager netcode = NetworkManager.Singleton;
            // The host takes ingredients from its copy of this player; make that copy current first.
            // Same delivery channel, so the upload arrives before the request.
            if (op == Op.Add || (op == Op.Work && Time.unscaledTime >= nextWorkUpload))
            {
                CoopPlayerProfiles.UploadNow("craft request");
                nextWorkUpload = Time.unscaledTime + 5f;
            }
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe((int)op);
                writer.WriteValueSafe(new FixedString64Bytes(WgoId(wgo)));
                writer.WriteValueSafe(new FixedString128Bytes(craftId ?? string.Empty));
                writer.WriteValueSafe(count);
                writer.WriteValueSafe(flag);
                writer.WriteValueSafe(queueIdx);
                writer.WriteValueSafe(new FixedString128Bytes(itemId ?? string.Empty));
                netcode.CustomMessagingManager.SendNamedMessage(RequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            requestsSent++;
            if (op != Op.Work)
            {
                log.LogInfo($"Craft sync: asked the host to {op} {craftId} x{count} at {Short(WgoId(wgo))}.");
            }
        }

        internal static void ReceiveState(ulong sender, FastBufferReader reader)
        {
            if (!Enabled || NetworkManager.Singleton == null || NetworkManager.Singleton.IsHost || sender != NetworkManager.ServerClientId)
            {
                return;
            }
            try
            {
                reader.ReadValueSafe(out FixedString64Bytes wgoText);
                reader.ReadValueSafe(out int length);
                if (length <= 0 || length > 64 * 1024)
                {
                    return;
                }
                byte[] compressed = new byte[length];
                reader.ReadBytesSafe(ref compressed, length);
                object wgo = FindWgo(wgoText.ToString());
                object component = wgo == null ? null : CoopDiagnostics.GetMember(wgo, "CraftComponent");
                if (component == null)
                {
                    return;
                }
                byte[] raw = Decompress(compressed);
                applying = true;
                try
                {
                    SerializerMethod("DeserializeInto").MakeGenericMethod(component.GetType()).Invoke(null, new[] { component, raw });
                    // Rebinds every queued element to this station and refreshes the recipe cache.
                    component.GetType().GetMethod("Init").Invoke(component, new[] { wgo });
                    RaiseProgressChanged(component);
                }
                finally
                {
                    applying = false;
                }
                statesApplied++;
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not apply a station state: " + Inner(ex).Message);
            }
        }

        /// <summary>Host-owned station input and zombie output, separate from CraftComponent state.</summary>
        internal static void ReceiveInventory(ulong sender, FastBufferReader reader)
        {
            if (!Enabled || NetworkManager.Singleton == null || NetworkManager.Singleton.IsHost || sender != NetworkManager.ServerClientId)
            {
                return;
            }
            try
            {
                reader.ReadValueSafe(out FixedString64Bytes wgoText);
                reader.ReadValueSafe(out int length);
                if (length <= 0 || length > 64 * 1024) return;
                byte[] compressed = new byte[length];
                reader.ReadBytesSafe(ref compressed, length);
                object wgo = FindWgo(wgoText.ToString());
                object inventory = wgo == null ? null : CoopDiagnostics.GetMember(wgo, "CraftableObjectCraftInventory");
                object data = CoopDiagnostics.GetMember(inventory, "Data");
                if (data == null) return;
                object received = CoopGameSerializer.Deserialize(data.GetType(), Decompress(compressed));
                FieldInfo itemsField = AccessTools.Field(data.GetType(), "inventory");
                if (itemsField == null) throw new MissingFieldException(data.GetType().Name, "inventory");
                object oldItems = itemsField.GetValue(data);
                object newItems = itemsField.GetValue(received);
                applying = true;
                try
                {
                    itemsField.SetValue(data, newItems);
                    inventory.GetType().GetMethod("NotifyItemsRemoved")?.Invoke(inventory, new[] { oldItems });
                    inventory.GetType().GetMethod("NotifyItemsAdded")?.Invoke(inventory, new[] { newItems });
                }
                finally
                {
                    applying = false;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not apply station inventory: " + Inner(ex).Message);
            }
        }

        /// <summary>A joiner taking completed output also removes it from the host's station.</summary>
        private static void InventoryTakePostfix(object __instance, object __result)
        {
            if (!Enabled || applying || !IsClient() || !CoopSession.Welcomed || !(__result is IEnumerable removed)) return;
            try
            {
                string stationId = null;
                foreach (KeyValuePair<string, object> station in SharedStations())
                {
                    object owner = CoopDiagnostics.GetMember(station.Value, "CraftableObject");
                    if (ReferenceEquals(__instance, CoopDiagnostics.GetMember(owner, "CraftableObjectCraftInventory")))
                    {
                        stationId = station.Key;
                        break;
                    }
                }
                if (stationId == null) return;
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (object item in removed)
                {
                    string id = Convert.ToString(CoopDiagnostics.GetMember(item, "id"));
                    int count = Convert.ToInt32(CoopDiagnostics.GetMember(item, "Count"));
                    if (string.IsNullOrEmpty(id) || count <= 0) continue;
                    counts[id] = (counts.TryGetValue(id, out int prior) ? prior : 0) + count;
                }
                foreach (KeyValuePair<string, int> entry in counts)
                {
                    using (var writer = new FastBufferWriter(256, Allocator.Temp))
                    {
                        writer.WriteValueSafe(new FixedString64Bytes(stationId));
                        writer.WriteValueSafe(new FixedString128Bytes(entry.Key));
                        writer.WriteValueSafe(entry.Value);
                        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(TakeMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not send station pickup: " + Inner(ex).Message);
            }
        }

        internal static void ReceiveTake(ulong sender, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsHost || sender == netcode.LocalClientId ||
                !netcode.ConnectedClientsIds.Contains(sender)) return;
            try
            {
                reader.ReadValueSafe(out FixedString64Bytes stationText);
                reader.ReadValueSafe(out FixedString128Bytes itemText);
                reader.ReadValueSafe(out int count);
                if (count <= 0 || count > 10000) return;
                object wgo = FindWgo(stationText.ToString());
                object component = wgo == null ? null : CoopDiagnostics.GetMember(wgo, "CraftComponent");
                int actual = 0;
                if (component != null && IsSharedStation(component, out _))
                {
                    object inventory = CoopDiagnostics.GetMember(wgo, "CraftableObjectCraftInventory");
                    MethodInfo remove = inventory.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .First(m => m.Name == "RemoveItemById" && m.GetParameters().Length == 5);
                    object removed = remove.Invoke(inventory, new object[] { itemText.ToString(), count, null, null, false });
                    if (removed is IEnumerable items)
                        foreach (object item in items) actual += Convert.ToInt32(CoopDiagnostics.GetMember(item, "Count"));
                    nextBroadcast = 0f;
                }
                SendTakeResult(netcode, sender, itemText.ToString(), count - actual);
                if (actual != count)
                    log.LogWarning($"Craft sync: station pickup conflict at {Short(stationText.ToString())}: {itemText} requested {count}, removed {actual}.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not apply station pickup from " + sender + ": " + Inner(ex).Message);
            }
        }

        private static void SendTakeResult(NetworkManager netcode, ulong clientId, string itemId, int shortfall)
        {
            using (var writer = new FastBufferWriter(160, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString128Bytes(itemId));
                writer.WriteValueSafe(shortfall);
                netcode.CustomMessagingManager.SendNamedMessage(TakeResultMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        /// <summary>Remove any item the joiner took locally that the host could not grant.</summary>
        internal static void ReceiveTakeResult(ulong sender, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || netcode.IsHost || sender != NetworkManager.ServerClientId) return;
            try
            {
                reader.ReadValueSafe(out FixedString128Bytes itemText);
                reader.ReadValueSafe(out int shortfall);
                if (shortfall <= 0 || shortfall > 10000) return;
                object player = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerData");
                object inventory = CoopDiagnostics.GetMember(player, "inventory");
                MethodInfo remove = inventory.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .First(m => m.Name == "RemoveItemById" && m.GetParameters().Length == 5);
                object removed = remove.Invoke(inventory, new object[] { itemText.ToString(), shortfall, null, null, false });
                int actual = 0;
                if (removed is IEnumerable items)
                    foreach (object item in items) actual += Convert.ToInt32(CoopDiagnostics.GetMember(item, "Count"));
                CoopPlayerProfiles.UploadNow("craft pickup correction");
                log.LogWarning($"Craft sync: host rejected {shortfall} {itemText} from a station pickup; removed {actual} from the local player.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not reconcile a station pickup: " + Inner(ex).Message);
            }
        }

        internal static void ReceiveConsume(ulong sender, FastBufferReader reader)
        {
            if (!Enabled || NetworkManager.Singleton == null || NetworkManager.Singleton.IsHost || sender != NetworkManager.ServerClientId)
            {
                return;
            }
            try
            {
                reader.ReadValueSafe(out int count);
                object inventory = CoopDiagnostics.GetMember(CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerData"), "inventory");
                MethodInfo remove = inventory.GetType().GetMethod("RemoveItemById");
                var parts = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out FixedString128Bytes id);
                    reader.ReadValueSafe(out int amount);
                    remove.Invoke(inventory, new object[] { id.ToString(), amount, null, null, false });
                    parts.Add(id + " x" + amount);
                }
                log.LogInfo("Craft sync: the host used " + string.Join(", ", parts.ToArray()) + " from this player's inventory.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not remove crafting ingredients: " + Inner(ex).Message);
            }
        }

        // ------------------------------------------------------------------ host side

        internal static void ReceiveRequest(ulong sender, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsHost || sender == netcode.LocalClientId)
            {
                return;
            }
            try
            {
                reader.ReadValueSafe(out int opValue);
                reader.ReadValueSafe(out FixedString64Bytes wgoText);
                reader.ReadValueSafe(out FixedString128Bytes craftText);
                reader.ReadValueSafe(out int count);
                reader.ReadValueSafe(out bool flag);
                reader.ReadValueSafe(out int queueIdx);
                reader.ReadValueSafe(out FixedString128Bytes itemText);
                var op = (Op)opValue;
                object wgo = FindWgo(wgoText.ToString());
                object component = wgo == null ? null : CoopDiagnostics.GetMember(wgo, "CraftComponent");
                if (component == null || !IsSharedStation(component, out _))
                {
                    log.LogWarning($"Craft sync: client {sender} asked to {op} at an unknown or unshared station {Short(wgoText.ToString())}.");
                    return;
                }
                string craftId = craftText.ToString();
                switch (op)
                {
                    case Op.Add:
                        object element = NewCraftElement(craftId, Math.Max(1, count), wgo);
                        object queued = component.GetType().GetMethod("AddToQueue").Invoke(component, new[] { element, (object)flag, queueIdx });
                        owners.Remove(queued ?? element);
                        owners.Add(queued ?? element, new Owner { ClientId = sender });
                        if (flag)
                        {
                            component.GetType().GetMethod("TryContinueFromQueue").Invoke(component, null);
                        }
                        break;
                    case Op.Remove:
                        var queue = (System.Collections.IList)CoopDiagnostics.GetMember(component, "CraftElementsQueue");
                        object target = count >= 0 && count < queue.Count && Convert.ToString(CoopDiagnostics.GetMember(queue[count], "CraftId")) == craftId
                            ? queue[count] : null;
                        if (target != null)
                        {
                            component.GetType().GetMethod("RemoveFromQueue").Invoke(component, new[] { target, (object)flag });
                        }
                        break;
                    case Op.Work:
                        MethodInfo updateManual = component.GetType().GetMethod("UpdateManual", new[] { typeof(int) });
                        int ticks = Math.Max(1, count);
                        // Ingredients for a craft that starts on this tick are reported by the owner path.
                        RunAsClient(sender, wgo, "craft work", false, () => updateManual.Invoke(component, new object[] { ticks }));
                        break;
                    case Op.SetCount:
                    {
                        var countQueue = (System.Collections.IList)CoopDiagnostics.GetMember(component, "CraftElementsQueue");
                        var counted = queueIdx >= 0 && queueIdx < countQueue.Count ? countQueue[queueIdx] as CraftElementBase : null;
                        if (counted == null || counted.CraftId != craftId)
                        {
                            log.LogWarning($"Craft sync: {CoopSession.NameFor(sender)} changed the count of {craftId} at place {queueIdx}, which the host's queue no longer has there.");
                            break;
                        }
                        counted.IsInfinite = flag;
                        // A count down to 0 is followed by the window's own removal, sent as its own request.
                        if (count > 0)
                        {
                            counted.Count = count;
                        }
                        break;
                    }
                    case Op.Cancel:
                        if (CoopDiagnostics.GetMember(component, "CurrentCraftElement") != null)
                        {
                            component.GetType().GetMethod("Cancel").Invoke(component, null);
                        }
                        break;
                }
                requestsApplied++;
                nextBroadcast = 0f;
                if (op != Op.Work)
                {
                    log.LogInfo($"Craft sync: {CoopSession.NameFor(sender)} {op} {craftId} x{count} at {Short(wgoText.ToString())}.");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not apply a craft request from client " + sender + ": " + Inner(ex).Message);
            }
        }

        /// <summary>
        /// Runs a joiner's action on the host as that joiner, standing at the station, so the game
        /// checks and takes that joiner's items. With <paramref name="reportConsumption"/>, whatever
        /// left the joiner's inventory is sent back for the joiner to remove; crafts started from
        /// the queue report through <see cref="RemoveRequirementsPrefix"/> instead, so the two never
        /// both report one removal.
        /// </summary>
        private static void RunAsClient(ulong clientId, object wgo, string label, bool reportConsumption, Action action)
        {
            object shadow = CoopDropSync.ResolvePlayerDataFor(clientId);
            NetworkManager netcode = NetworkManager.Singleton;
            if (shadow == null || netcode == null)
            {
                log.LogWarning("Craft sync: no player record for client " + clientId + "; " + label + " not applied.");
                return;
            }
            object inventory = CoopDiagnostics.GetMember(shadow, "inventory");
            Dictionary<string, int> before = reportConsumption ? Counts(inventory) : null;
            FieldInfo zoneField = AccessTools.Field(shadow.GetType(), "currentWorldZoneData");
            object previousZone = zoneField?.GetValue(shadow);
            CoopPlayerContext.Run(shadow, null, label + " by client " + clientId, delegate
            {
                zoneField?.SetValue(shadow, CoopDiagnostics.GetMember(wgo, "WorldZoneData"));
                try
                {
                    action();
                }
                finally
                {
                    zoneField?.SetValue(shadow, previousZone);
                }
            });
            if (!reportConsumption)
            {
                return;
            }
            Dictionary<string, int> after = Counts(inventory);
            var used = new Dictionary<string, int>();
            var list = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> entry in before)
            {
                after.TryGetValue(entry.Key, out int left);
                if (entry.Value > left)
                {
                    used[entry.Key] = entry.Value - left;
                    list.Add(new KeyValuePair<string, int>(entry.Key, entry.Value - left));
                }
            }
            if (list.Count > 0)
            {
                SendConsume(netcode, clientId, list);
                consumedForClients++;
            }
            log.LogInfo($"Craft sync: {label} for {CoopSession.NameFor(clientId)} at {Short(WgoId(wgo))}; used from their inventory: {Describe(used)}.");
        }

        /// <summary>
        /// A craft ordered by a joiner takes its ingredients as that joiner: their inventory, as
        /// if standing at the station (they were, when they ordered it). Whatever left their
        /// inventory is then removed on their own machine too.
        /// </summary>
        private static void StartPrefix(object craftElement)
        {
            startingOwner = craftElement != null && owners.TryGetValue(craftElement, out Owner owner) ? owner : null;
            if (startingOwner != null && ingredientLogsLeft > 0)
            {
                log.LogInfo("Craft sync: starting a craft ordered by client " + startingOwner.ClientId + ".");
            }
        }

        private static Exception StartFinalizer(Exception __exception)
        {
            startingOwner = null;
            return __exception;
        }

        private static bool RemoveRequirementsPrefix(object __instance, object craftElement)
        {
            Owner owner = null;
            if (reentrant || !Enabled || craftElement == null ||
                !(owners.TryGetValue(craftElement, out owner) || (owner = startingOwner) != null))
            {
                return true;
            }
            NetworkManager netcode = NetworkManager.Singleton;
            if (netcode == null || !netcode.IsHost || owner.ClientId == netcode.LocalClientId || !System.Linq.Enumerable.Contains(netcode.ConnectedClientsIds, owner.ClientId))
            {
                return true;
            }
            object shadow = CoopDropSync.ResolvePlayerDataFor(owner.ClientId);
            object wgo = CoopDiagnostics.GetMember(__instance, "CraftableObject");
            if (shadow == null || wgo == null)
            {
                log.LogWarning("Craft sync: no player record for client " + owner.ClientId + "; ingredients come from the host's side.");
                return true;
            }
            object inventory = CoopDiagnostics.GetMember(shadow, "inventory");
            Dictionary<string, int> before = Counts(inventory);
            FieldInfo zoneField = AccessTools.Field(shadow.GetType(), "currentWorldZoneData");
            object previousZone = zoneField?.GetValue(shadow);
            Action takeIngredients = delegate
            {
                zoneField?.SetValue(shadow, CoopDiagnostics.GetMember(wgo, "WorldZoneData"));
                reentrant = true;
                try
                {
                    removeRequirements.Invoke(__instance, new[] { craftElement });
                }
                finally
                {
                    reentrant = false;
                    zoneField?.SetValue(shadow, previousZone);
                }
            };
            bool ok;
            if (CoopPlayerContext.IsActive)
            {
                // Usually the start comes from the joiner's own replicated tool use, which already
                // runs as that joiner; the context cannot be nested, and need not be.
                bool sameActor = ReferenceEquals(CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerData"), shadow);
                if (!sameActor)
                {
                    log.LogWarning("Craft sync: a craft ordered by client " + owner.ClientId + " started during another player's action; ingredients follow that player.");
                    return true;
                }
                takeIngredients();
                ok = true;
            }
            else
            {
                ok = CoopPlayerContext.Run(shadow, null, "craft for client " + owner.ClientId, takeIngredients);
            }
            if (!ok)
            {
                return true;
            }
            Dictionary<string, int> after = Counts(inventory);
            var used = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> entry in before)
            {
                after.TryGetValue(entry.Key, out int left);
                if (entry.Value > left)
                {
                    used.Add(new KeyValuePair<string, int>(entry.Key, entry.Value - left));
                }
            }
            if (used.Count > 0)
            {
                SendConsume(netcode, owner.ClientId, used);
                consumedForClients++;
            }
            if (ingredientLogsLeft > 0)
            {
                ingredientLogsLeft--;
                var usedText = new List<string>();
                foreach (KeyValuePair<string, int> entry in used) usedText.Add(entry.Key + " x" + entry.Value);
                log.LogInfo($"Craft sync: took ingredients for client {owner.ClientId} as that player; from their inventory: " +
                            (usedText.Count == 0 ? "nothing" : string.Join(", ", usedText.ToArray())) +
                            $" (had {Describe(before)}; station/chests supplied the rest).");
            }
            return false;
        }

        private static void SendConsume(NetworkManager netcode, ulong clientId, List<KeyValuePair<string, int>> used)
        {
            using (var writer = new FastBufferWriter(64 + used.Count * 136, Allocator.Temp))
            {
                writer.WriteValueSafe(used.Count);
                foreach (KeyValuePair<string, int> entry in used)
                {
                    writer.WriteValueSafe(new FixedString128Bytes(entry.Key));
                    writer.WriteValueSafe(entry.Value);
                }
                netcode.CustomMessagingManager.SendNamedMessage(ConsumeMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        /// <summary>Driven from the plugin's one-second poll.</summary>
        internal static void Tick()
        {
            if (!Enabled || !IsHostWithClients() || Time.unscaledTime < nextBroadcast)
            {
                return;
            }
            nextBroadcast = Time.unscaledTime + 1f;
            if (broadcastQueue.Count > 0)
            {
                return;
            }
            try
            {
                foreach (KeyValuePair<string, object> station in SharedStations())
                {
                    broadcastQueue.Enqueue(station);
                }
            }
            catch (Exception ex)
            {
                broadcastQueue.Clear();
                log.LogWarning("Craft sync: station broadcast failed: " + Inner(ex).Message);
            }
        }

        // The stations to compare and send, queued once a second and worked through over the next
        // frames: serializing them all in one frame took 4 ms a second on average with three
        // joiners, up to 29 ms on a busy PC.
        private static readonly Queue<KeyValuePair<string, object>> broadcastQueue = new Queue<KeyValuePair<string, object>>();
        private static readonly long PumpBudget = System.Diagnostics.Stopwatch.Frequency / 1000;

        /// <summary>From the plugin's Update: about a millisecond of the queued stations each frame.</summary>
        internal static void Pump()
        {
            if (broadcastQueue.Count == 0)
            {
                return;
            }
            if (!Enabled || !IsHostWithClients())
            {
                broadcastQueue.Clear();
                return;
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                do
                {
                    KeyValuePair<string, object> station = broadcastQueue.Dequeue();
                    byte[] raw = Serialize(station.Value);
                    if (!lastSent.TryGetValue(station.Key, out byte[] previous) || !SameBytes(previous, raw))
                    {
                        lastSent[station.Key] = raw;
                        SendState(NetworkManager.Singleton, null, station.Key, raw);
                    }
                    byte[] inventoryRaw = SerializeInventory(station.Value);
                    if (inventoryRaw != null && (!lastInventorySent.TryGetValue(station.Key, out byte[] priorInventory) || !SameBytes(priorInventory, inventoryRaw)))
                    {
                        lastInventorySent[station.Key] = inventoryRaw;
                        SendInventory(NetworkManager.Singleton, null, station.Key, inventoryRaw);
                    }
                }
                while (broadcastQueue.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - start < PumpBudget);
            }
            catch (Exception ex)
            {
                broadcastQueue.Clear();
                log.LogWarning("Craft sync: station broadcast failed: " + Inner(ex).Message);
            }
        }

        /// <summary>A joiner's world is the host's save; stations changed since are sent on join.</summary>
        internal static void SendAllTo(ulong clientId)
        {
            if (!Enabled)
            {
                return;
            }
            int sent = 0;
            try
            {
                foreach (KeyValuePair<string, object> station in SharedStations())
                {
                    byte[] raw = Serialize(station.Value);
                    lastSent[station.Key] = raw;
                    SendState(NetworkManager.Singleton, clientId, station.Key, raw);
                    byte[] inventoryRaw = SerializeInventory(station.Value);
                    if (inventoryRaw != null)
                    {
                        lastInventorySent[station.Key] = inventoryRaw;
                        SendInventory(NetworkManager.Singleton, clientId, station.Key, inventoryRaw);
                    }
                    sent++;
                }
                log.LogInfo($"Craft sync: sent {sent} station states to client {clientId}.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft sync: could not send station states to client " + clientId + ": " + Inner(ex).Message);
            }
        }

        private static void SendState(NetworkManager netcode, ulong? target, string wgoId, byte[] raw)
        {
            byte[] compressed = Compress(raw);
            if (compressed.Length > 60 * 1024)
            {
                log.LogWarning($"Craft sync: station {Short(wgoId)} state is {compressed.Length} bytes; not sent.");
                return;
            }
            using (var writer = new FastBufferWriter(96 + compressed.Length, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString64Bytes(wgoId));
                writer.WriteValueSafe(compressed.Length);
                writer.WriteBytesSafe(compressed, compressed.Length);
                // Station states are small, but a long queue can pass one datagram.
                NetworkDelivery delivery = compressed.Length > 1000 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.ReliableSequenced;
                foreach (ulong clientId in netcode.ConnectedClientsIds)
                {
                    if (clientId != netcode.LocalClientId && (!target.HasValue || target.Value == clientId))
                    {
                        netcode.CustomMessagingManager.SendNamedMessage(StateMessage, clientId, writer, delivery);
                    }
                }
            }
        }

        private static byte[] SerializeInventory(object component)
        {
            object wgo = CoopDiagnostics.GetMember(component, "CraftableObject");
            object inventory = wgo == null ? null : CoopDiagnostics.GetMember(wgo, "CraftableObjectCraftInventory");
            object data = CoopDiagnostics.GetMember(inventory, "Data");
            return data == null ? null : CoopGameSerializer.Serialize(data);
        }

        private static void SendInventory(NetworkManager netcode, ulong? target, string wgoId, byte[] raw)
        {
            byte[] compressed = Compress(raw);
            if (compressed.Length > 60 * 1024)
            {
                log.LogWarning($"Craft sync: station {Short(wgoId)} inventory is {compressed.Length} bytes; not sent.");
                return;
            }
            using (var writer = new FastBufferWriter(96 + compressed.Length, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString64Bytes(wgoId));
                writer.WriteValueSafe(compressed.Length);
                writer.WriteBytesSafe(compressed, compressed.Length);
                NetworkDelivery delivery = compressed.Length > 1000 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.ReliableSequenced;
                foreach (ulong clientId in netcode.ConnectedClientsIds)
                {
                    if (clientId != netcode.LocalClientId && (!target.HasValue || target.Value == clientId))
                    {
                        netcode.CustomMessagingManager.SendNamedMessage(InventoryMessage, clientId, writer, delivery);
                    }
                }
            }
        }

        private static IEnumerable<KeyValuePair<string, object>> SharedStations()
        {
            // Read directly, not by reflection: on the host this goes through every object of the
            // world every second.
            MainGame mainGame = MainGame.Instance;
            GameSave save = mainGame == null ? null : mainGame.GameSave;
            List<GameSceneData> scenes = save == null || save.worldData == null ? null : save.worldData.gameSceneDataList;
            if (scenes == null)
            {
                yield break;
            }
            foreach (GameSceneData scene in scenes)
            {
                if (scene == null || scene.wgoDataList == null)
                {
                    continue;
                }
                foreach (WgoData wgo in scene.wgoDataList)
                {
                    CraftComponent component = wgo == null ? null : wgo.CraftComponent;
                    if (component != null && IsSharedStation(component, out _))
                    {
                        yield return new KeyValuePair<string, object>(WgoId(wgo), component);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// The element the host queues for a joiner, with the recipe's ingredients. The short
        /// constructor (id, count, params) leaves the requirement list empty, and a craft built
        /// that way consumes nothing — the first version handed joiners free crafts.
        /// </summary>
        private static object NewCraftElement(string craftId, int count, object wgo)
        {
            Type paramsType = Plugin.FindGameType("CraftParamsData");
            object parameters = Activator.CreateInstance(paramsType, craftId, wgo, Enum.ToObject(paramsType.GetNestedType("CraftParamsType"), 0), -1);
            object definition = AccessTools.Method(Plugin.FindGameType("GameBalance"), "GetCraftDefBase", new[] { typeof(string) }).Invoke(null, new object[] { craftId });
            if (definition == null)
            {
                throw new InvalidOperationException("unknown craft " + craftId);
            }
            Type needList = typeof(List<>).MakeGenericType(Plugin.FindGameType("NeedItemData"));
            object needs = Activator.CreateInstance(needList, CoopDiagnostics.GetMember(definition, "needItems"));
            return Activator.CreateInstance(Plugin.FindGameType("CraftElement"), craftId, count, needs, parameters);
        }

        private static int QueueIndex(object component, object element)
        {
            var queue = CoopDiagnostics.GetMember(component, "CraftElementsQueue") as System.Collections.IList;
            return queue == null ? -1 : queue.IndexOf(element);
        }

        private static void RaiseProgressChanged(object component)
        {
            FieldInfo handler = AccessTools.Field(component.GetType(), "OnCraftCurProgressNormalizedChanged");
            object current = CoopDiagnostics.GetMember(component, "CurrentCraftElement");
            if (handler?.GetValue(component) is Delegate callback && current != null)
            {
                callback.DynamicInvoke(Convert.ToSingle(CoopDiagnostics.GetMember(current, "ProgressTimeNormalized")));
            }
        }

        private static string Describe(Dictionary<string, int> counts)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> entry in counts) parts.Add(entry.Key + " x" + entry.Value);
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
        }

        private static Dictionary<string, int> Counts(object inventory)
        {
            var counts = new Dictionary<string, int>();
            object data = CoopDiagnostics.GetMember(inventory, "Data");
            if (CoopDiagnostics.GetMember(data, "Inventory") is System.Collections.IEnumerable items)
            {
                foreach (object item in items)
                {
                    string id = Convert.ToString(CoopDiagnostics.GetMember(item, "id"));
                    counts.TryGetValue(id, out int existing);
                    counts[id] = existing + Convert.ToInt32(CoopDiagnostics.GetMember(item, "Count"));
                }
            }
            return counts;
        }

        private static object FindWgo(string id)
        {
            object mainGame = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance");
            object save = mainGame == null ? null : CoopDiagnostics.GetMember(mainGame, "GameSave");
            object world = save == null ? null : CoopDiagnostics.GetMember(save, "worldData");
            Type sguid = Plugin.FindGameType("SGuid");
            MethodInfo parse = AccessTools.Method(sguid, "Parse");
            MethodInfo get = world == null ? null : AccessTools.Method(world.GetType(), "GetWgoData", new[] { sguid });
            return parse == null || get == null ? null : get.Invoke(world, new[] { parse.Invoke(null, new object[] { id }) });
        }

        private static string WgoId(object wgo)
        {
            return Convert.ToString(CoopDiagnostics.GetMember(CoopDiagnostics.GetMember(wgo, "UniqueId"), "Id"));
        }

        // The serializer for each station type, made once: every shared station goes through it every second.
        private static readonly Dictionary<Type, MethodInfo> serializeFor = new Dictionary<Type, MethodInfo>();

        private static byte[] Serialize(object component)
        {
            Type type = component.GetType();
            if (!serializeFor.TryGetValue(type, out MethodInfo serialize))
            {
                serialize = SerializerMethod("Serialize").MakeGenericMethod(type);
                serializeFor[type] = serialize;
            }
            return (byte[])serialize.Invoke(null, new[] { component });
        }

        private static MethodInfo SerializerMethod(string name)
        {
            Type serializer = Plugin.FindGameType("LazyBearTechnology.LazySerializer");
            foreach (MethodInfo method in serializer.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (method.Name != name || !method.IsGenericMethodDefinition)
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                if ((name == "Serialize" && parameters.Length == 1) ||
                    (name == "DeserializeInto" && parameters.Length == 2 && parameters[1].ParameterType == typeof(byte[])))
                {
                    return method;
                }
            }
            throw new MissingMethodException("LazySerializer." + name);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] Compress(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }
                return output.ToArray();
            }
        }

        private static byte[] Decompress(byte[] compressed)
        {
            using (var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        private static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "?" : (id.Length > 8 ? id.Substring(0, 8) : id);
        }

        private static Exception Inner(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex;
        }
    }
}
