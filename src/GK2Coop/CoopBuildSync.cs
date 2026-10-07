using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace GK2Coop
{
    /// <summary>
    /// What one player builds or tears down in build mode, every player sees.
    ///
    /// Placing a building (<c>WgoBuildPointer.TryDoBuildAction</c>) creates a new world object on
    /// the builder's machine only; removing one in build mode (<c>RemovePointer.TryDoBuildAction</c>
    /// → <c>WgoData.DoBuildRemove</c>) deletes it there with <c>RemoveWgoDataFromGameScene</c>,
    /// which is not a death, so world sync never saw either.
    ///
    /// A placement sends the new object whole — serialized with the game's save serializer after
    /// the building's own after-build expressions ran, including any workbench it extends. The
    /// receiver adds it through <c>WorldData.AddWgoData</c>, which spawns its view when the scene is
    /// loaded; the object keeps its id on every machine, so later crafts, repairs and deaths on it
    /// line up. A removal sends the object's id. Building materials and dropped contents are
    /// handled where they happen (the builder's inventory, drop sync). The host passes both on.
    /// </summary>
    internal static class CoopBuildSync
    {
        internal const string BuildMessage = "GK2Coop.Build.v1";

        private const byte KindPlace = 0;
        private const byte KindRemove = 1;
        // A fight building (barricade, flag stand) placed in a fighting level's pre-fight: the
        // receiver also lists it with the military base, as the builder's game did.
        private const byte KindPlaceFight = 2;

        private static ManualLogSource log;
        private static int sent;
        private static int applied;
        private static bool applying;
        private static string removing;
        private static bool placing;
        private static object placed;

        internal static bool Enabled { get; set; } = true;

        internal static string Describe()
        {
            return "build sync: sent=" + sent + ", applied=" + applied;
        }

        internal static void Init(ManualLogSource source)
        {
            log = source;
        }

        internal static void Install(Harmony harmony)
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                MethodInfo place = AccessTools.Method(Plugin.FindGameType("WgoBuildPointer"), "TryDoBuildAction");
                MethodInfo remove = AccessTools.Method(Plugin.FindGameType("RemovePointer"), "TryDoBuildAction");
                if (place == null || remove == null)
                {
                    log.LogWarning("Build sync: build-mode actions not found; building stays local.");
                    Enabled = false;
                    return;
                }
                MethodInfo sceneAdd = AccessTools.Method(Plugin.FindGameType("GameScene"), "AddWgoData", new[] { Plugin.FindGameType("WgoData"), typeof(bool) });
                if (sceneAdd == null)
                {
                    log.LogWarning("Build sync: GameScene.AddWgoData not found; building stays local.");
                    Enabled = false;
                    return;
                }
                harmony.Patch(place,
                    prefix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(PlacePrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(PlacePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                // The pointer adds the new object through its scene; that is where it is caught.
                harmony.Patch(sceneAdd, postfix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(SceneAddPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                // Fight buildings have their own pointer, which overrides the placement entirely.
                MethodInfo placeFight = AccessTools.DeclaredMethod(Plugin.FindGameType("FightingBuildPointer"), "TryDoBuildAction");
                if (placeFight != null)
                {
                    harmony.Patch(placeFight,
                        prefix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(PlacePrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(PlaceFightPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                harmony.Patch(remove,
                    prefix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(RemovePrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(CoopBuildSync).GetMethod(nameof(RemovePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                log.LogInfo("Build sync: buildings placed or removed in build mode are shared.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                log.LogWarning("Build sync disabled: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ capture

        private static void PlacePrefix()
        {
            placing = true;
            placed = null;
        }

        private static void SceneAddPostfix(object data)
        {
            if (placing && placed == null)
            {
                placed = data;
            }
        }

        private static void PlacePostfix(bool __result)
        {
            SharePlaced(__result, KindPlace);
        }

        private static void PlaceFightPostfix(bool __result)
        {
            SharePlaced(__result, KindPlaceFight);
        }

        private static void SharePlaced(bool __result, byte kind)
        {
            placing = false;
            object built = placed;
            placed = null;
            NetworkManager netcode = NetworkManager.Singleton;
            if (!__result || built == null || applying || netcode == null || !netcode.IsListening)
            {
                return;
            }
            try
            {
                Send(netcode, null, kind, IdOf(built), null, Compress(CoopGameSerializer.Serialize(built)));
                log.LogInfo("Build sync: shared a new " + CoopDiagnostics.GetMember(built, "id") + " " + Short(IdOf(built)) + ".");
            }
            catch (Exception ex)
            {
                log.LogWarning("Build sync: could not share a placed building: " + Inner(ex).Message);
            }
        }

        private static void RemovePrefix(object __instance)
        {
            removing = null;
            if (applying)
            {
                return;
            }
            try
            {
                object selection = AccessTools.Field(__instance.GetType(), "currentRemovingSelection")?.GetValue(__instance);
                object data = selection == null ? null : CoopDiagnostics.GetMember(selection, "Data");
                removing = data == null ? null : IdOf(data);
            }
            catch
            {
                removing = null;
            }
        }

        private static void RemovePostfix(bool __result)
        {
            string id = removing;
            removing = null;
            NetworkManager netcode = NetworkManager.Singleton;
            if (!__result || id == null || applying || netcode == null || !netcode.IsListening)
            {
                return;
            }
            // A removal that starts a deconstruction craft returns true without removing yet; only
            // an object that is actually gone is announced (the craft's end is craft-end sync's).
            if (FindWgo(id) != null)
            {
                return;
            }
            try
            {
                Send(netcode, null, KindRemove, id, null, new byte[0]);
                log.LogInfo("Build sync: shared the removal of " + Short(id) + ".");
            }
            catch (Exception ex)
            {
                log.LogWarning("Build sync: could not share a removal: " + Inner(ex).Message);
            }
        }

        /// <summary>
        /// Tests: the capture path around the game's own scene add and removal, without the
        /// build-mode UI that drives it in play.
        /// </summary>
        internal static void TestPlace(object gameScene, object wgoData)
        {
            PlacePrefix();
            gameScene.GetType().GetMethod("AddWgoData", new[] { Plugin.FindGameType("WgoData"), typeof(bool) }).Invoke(gameScene, new[] { wgoData, (object)false });
            PlacePostfix(true);
        }

        /// <summary>Tests: a fight building placed as the pre-fight's pointer places it.</summary>
        internal static void TestPlaceFight(object gameScene, object wgoData)
        {
            PlacePrefix();
            gameScene.GetType().GetMethod("AddWgoData", new[] { Plugin.FindGameType("WgoData"), typeof(bool) }).Invoke(gameScene, new[] { wgoData, (object)false });
            PlaceFightPostfix(true);
        }

        internal static void TestRemove(object wgoData)
        {
            removing = IdOf(wgoData);
            object world = WorldData();
            world.GetType().GetMethod("RemoveWgoDataFromGameScene", new[] { Plugin.FindGameType("WgoData"), typeof(bool) }).Invoke(world, new[] { wgoData, (object)true });
            RemovePostfix(true);
        }

        // ------------------------------------------------------------------ wire

        // ------------------------------------------------------------------ join gap
        // A friend joining copies the host's world at the request and connects in it some seconds
        // later; a building placed or removed in between reached them in the main menu, where it
        // went nowhere, and was missing for good (found by the `mix` run). The host keeps this
        // session's building changes and plays them to every joiner when they are let in, with the
        // rest of the join snapshot. Playing one again changes nothing: a placed building that is
        // already there is skipped, a removed one that is gone is gone.
        private sealed class Change
        {
            internal byte Kind;
            internal string Id;
            internal string Parent;
            internal byte[] Payload;
        }

        private static readonly List<Change> journal = new List<Change>();
        private const int JournalLimit = 1000;

        private static void Journal(byte kind, string id, string parent, byte[] payload)
        {
            if (journal.Count >= JournalLimit)
            {
                journal.RemoveAt(0);
            }
            journal.Add(new Change { Kind = kind, Id = id, Parent = parent, Payload = payload });
        }

        /// <summary>At the end of a hosted session.</summary>
        internal static void ResetJournal()
        {
            journal.Clear();
        }

        /// <summary>The join snapshot: this session's building changes, to one joiner, in order.</summary>
        internal static int SendSnapshotTo(ulong clientId)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsHost || journal.Count == 0)
            {
                return 0;
            }
            foreach (Change change in journal)
            {
                using (var writer = new FastBufferWriter(256 + change.Payload.Length, Allocator.Temp))
                {
                    writer.WriteValueSafe(change.Kind);
                    writer.WriteValueSafe(new FixedString64Bytes(change.Id));
                    writer.WriteValueSafe(new FixedString64Bytes(change.Parent ?? string.Empty));
                    writer.WriteValueSafe(change.Payload.Length);
                    writer.WriteBytesSafe(change.Payload, change.Payload.Length);
                    NetworkDelivery delivery = change.Payload.Length > 1000 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.ReliableSequenced;
                    netcode.CustomMessagingManager.SendNamedMessage(BuildMessage, clientId, writer, delivery);
                }
            }
            return journal.Count;
        }

        private static void Send(NetworkManager netcode, ulong? except, byte kind, string id, string parent, byte[] payload)
        {
            using (var writer = new FastBufferWriter(256 + payload.Length, Allocator.Temp))
            {
                writer.WriteValueSafe(kind);
                writer.WriteValueSafe(new FixedString64Bytes(id));
                writer.WriteValueSafe(new FixedString64Bytes(parent ?? string.Empty));
                writer.WriteValueSafe(payload.Length);
                writer.WriteBytesSafe(payload, payload.Length);
                NetworkDelivery delivery = payload.Length > 1000 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.ReliableSequenced;
                if (netcode.IsHost)
                {
                    Journal(kind, id, parent, payload);
                    foreach (ulong clientId in netcode.ConnectedClientsIds)
                    {
                        if (clientId != netcode.LocalClientId && clientId != except)
                        {
                            netcode.CustomMessagingManager.SendNamedMessage(BuildMessage, clientId, writer, delivery);
                        }
                    }
                }
                else
                {
                    netcode.CustomMessagingManager.SendNamedMessage(BuildMessage, NetworkManager.ServerClientId, writer, delivery);
                }
            }
            sent++;
        }

        internal static void Receive(ulong sender, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsListening)
            {
                return;
            }
            byte kind = 255;
            string id = "?";
            try
            {
                reader.ReadValueSafe(out kind);
                reader.ReadValueSafe(out FixedString64Bytes idText);
                reader.ReadValueSafe(out FixedString64Bytes parentText);
                reader.ReadValueSafe(out int length);
                if (length < 0 || length > 1024 * 1024)
                {
                    return;
                }
                byte[] payload = new byte[length];
                if (length > 0)
                {
                    reader.ReadBytesSafe(ref payload, length);
                }
                id = idText.ToString();
                if (netcode.IsHost)
                {
                    Send(netcode, sender, kind, id, parentText.ToString(), payload);
                }
                applying = true;
                try
                {
                    if (kind == KindPlace || kind == KindPlaceFight)
                    {
                        object built = Place(id, parentText.ToString(), payload);
                        if (built != null && kind == KindPlaceFight)
                        {
                            ListWithMilitaryBase(built);
                        }
                    }
                    else if (kind == KindRemove)
                    {
                        Remove(id);
                    }
                }
                finally
                {
                    applying = false;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Build sync: could not apply a building change from " + sender + " (kind " + kind + ", " + id + "): " + Inner(ex).Message + " at " + CoopDiagnostics.FirstFrames(Inner(ex)));
            }
        }

        private static object Place(string id, string parent, byte[] payload)
        {
            if (FindWgo(id) != null)
            {
                return null;
            }
            object data = CoopGameSerializer.Deserialize(Plugin.FindGameType("WgoData"), Decompress(payload));
            if (data == null)
            {
                return null;
            }
            // A deserialized object is set up as the game sets up every object when a save loads:
            // its parts' dock points, craft, movement and health components. Without it a building
            // mirrored from another player was half made here until the next load — the garden
            // bed's navigation and a zombie assigned to the station threw on the missing dock points.
            data.GetType().GetMethod("PrepareForGame", Type.EmptyTypes)?.Invoke(data, null);
            object world = WorldData();
            world.GetType().GetMethod("AddWgoData", new[] { Plugin.FindGameType("WgoData"), typeof(bool) }).Invoke(world, new[] { data, (object)false });
            // A workbench extension: the placed object names its workbench; the workbench's side of
            // the link is made here, as the builder's machine made it.
            if (CoopDiagnostics.GetMember(data, "workbenchParents") is System.Collections.IEnumerable parents)
            {
                foreach (object parentGuid in parents)
                {
                    if (FindWgo(Convert.ToString(CoopDiagnostics.GetMember(parentGuid, "Id"))) is object parentData)
                    {
                        parentData.GetType().GetMethod("AddWorkbenchExtension")?.Invoke(parentData, new[] { CoopDiagnostics.GetMember(data, "UniqueId") });
                    }
                }
            }
            SyncQualityRects(data, true);
            applied++;
            log.LogInfo("Build sync: built " + CoopDiagnostics.GetMember(data, "id") + " " + Short(id) + " as another player did.");
            return data;
        }

        /// <summary>
        /// As <c>FightingBuildPointer</c> does after placing: the base lists the building, and makes
        /// it a defender. That needs its view, which exists only where its scene is shown here;
        /// elsewhere the building is placed and listed later by the game's own teardown.
        /// </summary>
        private static void ListWithMilitaryBase(object built)
        {
            Type gameScene = Plugin.FindGameType("GameScene");
            object uniqueId = CoopDiagnostics.GetMember(built, "UniqueId");
            MethodInfo view = gameScene == null || uniqueId == null ? null : gameScene.GetMethod("GetWgoViewGlobal", new[] { uniqueId.GetType() });
            if (view == null || view.Invoke(null, new[] { uniqueId }) == null)
            {
                log.LogInfo("Build sync: fight building " + Short(IdOf(built)) + " placed; not listed with the base here (not in view).");
                return;
            }
            object save = CoopDiagnostics.GetMember(CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance"), "GameSave");
            object militaryBase = CoopDiagnostics.GetMember(save, "militaryBaseData");
            militaryBase?.GetType().GetMethod("AddFightBuilding")?.Invoke(militaryBase, new[] { built });
        }

        private static void Remove(string id)
        {
            object data = FindWgo(id);
            if (data == null)
            {
                return;
            }
            SyncQualityRects(data, false);
            object world = WorldData();
            world.GetType().GetMethod("RemoveWgoDataFromGameScene", new[] { Plugin.FindGameType("WgoData"), typeof(bool) }).Invoke(world, new[] { data, (object)true });
            applied++;
            log.LogInfo("Build sync: removed " + Short(id) + " as another player did.");
        }

        // ------------------------------------------------------------------ quality areas
        // A graveyard module's after-build expression registers its area with the graveyard zone
        // (AddCustomRectToWZ); only graves inside a registered area count toward the zone's quality.
        // That changes the zone, not the object, so a module mirrored from another player came
        // without its area and its graves never counted here. The area call alone is run again —
        // not the whole expression, whose other effects the object already carries.

        private static readonly Regex AddRectCall = new Regex(@"AddCustomRectToWZ\([^()]*\)");
        private static int repairedGeneration = -1;

        /// <summary>
        /// Registers (or unregisters) the quality areas of one building. Removes first:
        /// <c>AddCustomQualityRect</c> is not idempotent and would count the area's graves twice.
        /// </summary>
        internal static int SyncQualityRects(object wgoData, bool add)
        {
            if (!(wgoData is WgoData data) || data.Definition == null || !data.Definition.TryGetBuildingDefForWgo(out BuildingDef def))
            {
                return 0;
            }
            int calls = 0;
            foreach (LazyExpression expression in def.expressionAfterBuilding)
            {
                string raw = expression?.GetRawExpressionString();
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                foreach (Match call in AddRectCall.Matches(raw))
                {
                    string addCall = call.Value;
                    new LazyExpression(addCall.Replace("AddCustomRectToWZ", "RemoveCustomRectFromWZ")).EvaluateBool(data);
                    if (add)
                    {
                        new LazyExpression(addCall).EvaluateBool(data);
                    }
                    calls++;
                }
            }
            return calls;
        }

        /// <summary>
        /// Once per hosted session, when the world is loaded: registers the areas that modules built
        /// by other players in earlier sessions never got on this machine. Saves from before this
        /// fix carry such modules; areas that are already there are left as they are.
        /// </summary>
        internal static void TickRepairQualityRects(int generation)
        {
            if (!Enabled || repairedGeneration == generation)
            {
                return;
            }
            MainGame mainGame = MainGame.Instance;
            List<GameSceneData> scenes = mainGame?.GameSave?.worldData?.gameSceneDataList;
            if (mainGame == null || mainGame.gameState != MainGame.GameState.InGame || scenes == null || scenes.Count == 0)
            {
                return;
            }
            repairedGeneration = generation;
            int buildings = 0;
            try
            {
                foreach (GameSceneData scene in scenes)
                {
                    if (scene?.wgoDataList == null)
                    {
                        continue;
                    }
                    foreach (WgoData wgo in scene.wgoDataList.ToArray())
                    {
                        if (SyncQualityRects(wgo, true) > 0)
                        {
                            buildings++;
                        }
                    }
                }
                log.LogInfo("Build sync: registered the quality areas of " + buildings + " buildings.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Build sync: could not register quality areas: " + Inner(ex).Message);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static object FindWgo(string id)
        {
            object world = WorldData();
            Type sguid = Plugin.FindGameType("SGuid");
            MethodInfo parse = AccessTools.Method(sguid, "Parse");
            MethodInfo get = world == null ? null : AccessTools.Method(world.GetType(), "GetWgoData", new[] { sguid });
            return parse == null || get == null ? null : get.Invoke(world, new[] { parse.Invoke(null, new object[] { id }) });
        }

        private static object WorldData()
        {
            object mainGame = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance");
            object save = mainGame == null ? null : CoopDiagnostics.GetMember(mainGame, "GameSave");
            return save == null ? null : CoopDiagnostics.GetMember(save, "worldData");
        }

        private static string IdOf(object wgoData)
        {
            return Convert.ToString(CoopDiagnostics.GetMember(CoopDiagnostics.GetMember(wgoData, "UniqueId"), "Id"));
        }

        private static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "?" : (id.Length > 8 ? id.Substring(0, 8) : id);
        }

        private static byte[] Compress(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
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
