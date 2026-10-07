using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace GK2Coop
{
    /// <summary>
    /// Shared world drops with host-authoritative pickup.
    ///
    /// Until now a mirrored death produced its own drop on each machine, so a single bush yielded
    /// to both players. That kept everyone unblocked but duplicated resources. Here the drop is
    /// created once, on the machine whose action caused it, and mirrored to the peer carrying the
    /// same identity. A pickup is a request: only the host credits a player and changes the drop,
    /// then sends that canonical result to every peer.
    ///
    /// Identity is the problem to solve: <c>DropData.UniqueId</c> comes from the item's
    /// <c>SGuid</c>, which is generated per machine, so the two copies would otherwise have no
    /// common name. The originating machine's id is forced onto the mirrored copy — the same
    /// approach the shipped <c>WorldDataCommand</c> takes for world objects, where the host assigns
    /// and the client adopts.
    ///
    /// While this is on, a mirrored death must not also generate its own drops, or both mechanisms
    /// would spawn one. Enabling it therefore forces <c>RemoteDeathDrops</c> off.
    /// </summary>
    internal static class CoopDropSync
    {
        internal const string SpawnMessage = "GK2Coop.DropSpawn.v1";
        internal const string PickupRequestMessage = "GK2Coop.DropPickupRequest.v2";
        internal const string PickupResultMessage = "GK2Coop.DropPickupResult.v2";
        internal const string MergeMessage = "GK2Coop.DropMerge.v1";

        private static ManualLogSource log;
        private static int spawnsSent;
        private static int spawnsApplied;
        private static int pickupRequestsSent;
        private static int pickupRequestsReceived;
        private static int pickupAccepted;
        private static int pickupRejected;
        private static int pickupResultsApplied;
        private static int unresolved;
        private static int outgoingSequence;
        private static readonly HashSet<string> mirrored = new HashSet<string>();
        private static readonly HashSet<string> completedPickups = new HashSet<string>();
        private static int detailedLogsLeft = 12;

        internal static bool Enabled { get; set; }

        /// <summary>True while creating or removing a mirrored drop, so it cannot echo back.</summary>
        private static bool applyingRemote;

        internal static void Init(ManualLogSource source)
        {
            log = source;
        }

        internal static string Describe()
        {
            return "drop sync: spawns sent=" + spawnsSent + "/applied=" + spawnsApplied +
                   ", pickup requests sent/received=" + pickupRequestsSent + "/" + pickupRequestsReceived +
                   ", accepted=" + pickupAccepted + ", rejected=" + pickupRejected +
                   ", results applied=" + pickupResultsApplied + ", merges sent/applied=" +
                   mergesSent + "/" + mergesApplied + ", big takes sent/applied=" + bigTakesSent + "/" + bigTakesApplied +
                   ", unresolved=" + unresolved;
        }

        internal static void Install(Harmony harmony)
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                Type dropSystem = Plugin.FindGameType("DropSystem");
                MethodInfo intoWorld = dropSystem == null
                    ? null
                    : AccessTools.Method(dropSystem, "DropItemIntoWorld");
                if (intoWorld == null)
                {
                    log.LogWarning("DropSystem.DropItemIntoWorld not found; drops will not be shared.");
                    Enabled = false;
                    return;
                }
                harmony.Patch(intoWorld, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CoopDropSync), nameof(DropIntoWorldPostfix))));

                Type playerData = Plugin.FindGameType("PlayerData");
                MethodInfo collect = playerData == null ? null : AccessTools.Method(playerData, "CollectDrop");
                if (collect != null)
                {
                    harmony.Patch(collect, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CoopDropSync), nameof(CollectDropPrefix))));
                }

                // DropCollector.CollectGameResDropsFromList calls CollectResDrop directly when a
                // resource drop has no view, which would bypass arbitration and credit both peers.
                // CollectDrop's own dispatch into it never runs, because that prefix returns false.
                MethodInfo collectRes = playerData == null ? null : AccessTools.Method(playerData, "CollectResDrop");
                if (collectRes != null)
                {
                    harmony.Patch(collectRes, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CoopDropSync), nameof(CollectDropPrefix))));
                }

                // Drops lying near each other merge on contact: one DropView absorbs the other's
                // items and the absorbed DropData is removed. Only creation and pickup were
                // replicated, so a merge on one machine left the peer holding two separate drops
                // while this one held a single larger stack. Each machine also merges
                // independently under physics and can pick a different survivor, so the host
                // decides here exactly as it does for pickup.
                Type dropView = Plugin.FindGameType("DropView");
                MethodInfo merge = dropView == null ? null : AccessTools.Method(dropView, "Merge");
                if (merge != null)
                {
                    harmony.Patch(merge,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CoopDropSync), nameof(MergePrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CoopDropSync), nameof(MergePostfix))));
                }
                else
                {
                    log.LogWarning("DropView.Merge not found; merged drops will not be replicated.");
                }
                MethodInfo bigTake = AccessTools.Method(typeof(BigDropInteractionHandler), nameof(BigDropInteractionHandler.Interact));
                if (bigTake != null && BigDropField != null)
                {
                    harmony.Patch(bigTake,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CoopDropSync), nameof(BigTakePrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CoopDropSync), nameof(BigTakePostfix))));
                }
                else
                {
                    log.LogWarning("BigDropInteractionHandler.Interact not found; taking a big drop is not shared.");
                }
                log.LogInfo("Patched drops for sharing (spawn plus host-authoritative pickup).");
            }
            catch (Exception ex)
            {
                Enabled = false;
                log.LogWarning("Could not patch drops: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ spawn

        private static void DropIntoWorldPostfix(object[] __args)
        {
            if (!Enabled || applyingRemote)
            {
                return;
            }
            try
            {
                NetworkManager netcode = NetworkManager.Singleton;
                if (netcode == null || !netcode.IsListening || !netcode.IsConnectedClient)
                {
                    return;
                }
                object item = __args != null && __args.Length > 0 ? __args[0] : null;
                string worldId = __args != null && __args.Length > 1 ? Convert.ToString(__args[1]) : null;
                object rawPos = __args != null && __args.Length > 2 ? __args[2] : null;
                if (item == null || string.IsNullOrEmpty(worldId) || !(rawPos is Vector3))
                {
                    return;
                }

                string uniqueId = ReadItemGuid(item);
                string itemId = Convert.ToString(CoopDiagnostics.GetMember(item, "id"));
                int count = Convert.ToInt32(CoopDiagnostics.GetMember(item, "Count"));
                if (string.IsNullOrEmpty(uniqueId) || string.IsNullOrEmpty(itemId))
                {
                    return;
                }
                var position = (Vector3)rawPos;
                // A new drop can carry an id this session already finished: organs taken out of a
                // body keep their item's id, and so does a carried body put down again. Peers
                // ignored it and the host turned every pickup of it down, so it hung in the air.
                bool redrop = completedPickups.Remove(uniqueId) | mirrored.Remove(uniqueId);

                byte[] whole = WholeItem(item);
                byte[] zombie = ZombieOf(item);
                using (var writer = new FastBufferWriter(1024 + (whole?.Length ?? 0) + (zombie?.Length ?? 0), Allocator.Temp))
                {
                    writer.WriteValueSafe(++outgoingSequence);
                    writer.WriteValueSafe(new FixedString128Bytes(uniqueId));
                    writer.WriteValueSafe(new FixedString128Bytes(itemId));
                    writer.WriteValueSafe(count);
                    writer.WriteValueSafe(new FixedString128Bytes(worldId));
                    writer.WriteValueSafe(position);
                    // The world object whose harvest this is (empty for other drops); read by 0.65.3 on.
                    writer.WriteValueSafe(new FixedString128Bytes(CoopWorldSync.LocalDeathOrigin ?? string.Empty));
                    WriteBlob(writer, whole);
                    WriteBlob(writer, zombie);
                    writer.WriteValueSafe(redrop);
                    Broadcast(netcode, SpawnMessage, writer);
                }
                spawnsSent++;
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not report a drop: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ merge

        private static int mergesSent;
        private static int mergesApplied;

        /// <summary>
        /// Only the host merges. A client's own merge would race the host's and could pick the
        /// other survivor, leaving the two machines with different drop identities.
        /// </summary>
        /// <summary>What a merge on the host started from: the absorbed drop and both counts.</summary>
        internal struct MergeState
        {
            internal string AbsorbedId;
            internal object Absorbed;
            internal int SurvivorBefore;
            internal int AbsorbedBefore;
        }

        private static bool MergePrefix(object __instance, object[] __args, ref MergeState __state)
        {
            __state = default(MergeState);
            if (!Enabled || applyingRemote)
            {
                return true;
            }
            NetworkManager netcode = NetworkManager.Singleton;
            if (!SessionIsLive(netcode))
            {
                return true;
            }
            if (!netcode.IsHost)
            {
                return false;
            }
            object absorbed = __args != null && __args.Length > 0 ? __args[0] : null;
            __state.Absorbed = absorbed;
            __state.AbsorbedId = ReadDropGuid(CoopDiagnostics.GetMember(absorbed, "Data") ?? absorbed);
            __state.SurvivorBefore = DropCount(__instance);
            __state.AbsorbedBefore = DropCount(absorbed);
            return true;
        }

        private static int DropCount(object view)
        {
            object data = CoopDiagnostics.GetMember(view, "Data");
            object count = data == null ? null : CoopDiagnostics.GetMember(data, "Count");
            return count == null ? -1 : Convert.ToInt32(count);
        }

        private static void MergePostfix(object __instance, MergeState __state)
        {
            if (string.IsNullOrEmpty(__state.AbsorbedId))
            {
                return;
            }
            // The game tries a merge on every physics step for drops that touch, also when they
            // cannot merge (other items). Only a merge that moved items is news: reporting every
            // try searched all drops and sent the other players a message each time, hundreds a
            // second where drops of different items lay on each other.
            if (DropCount(__instance) == __state.SurvivorBefore && DropCount(__state.Absorbed) == __state.AbsorbedBefore)
            {
                return;
            }
            string absorbedId = __state.AbsorbedId;
            try
            {
                NetworkManager netcode = NetworkManager.Singleton;
                if (!SessionIsLive(netcode) || !netcode.IsHost)
                {
                    return;
                }
                object survivor = CoopDiagnostics.GetMember(__instance, "Data");
                string survivorId = ReadDropGuid(survivor);
                if (string.IsNullOrEmpty(survivorId))
                {
                    return;
                }
                int survivorCount = Convert.ToInt32(CoopDiagnostics.GetMember(survivor, "Count"));
                // The absorbed drop is only gone when it gave up everything; a partial merge
                // leaves it in the world with a smaller stack.
                object absorbed = FindDrop(absorbedId);
                int absorbedCount = absorbed == null ? 0 : Convert.ToInt32(CoopDiagnostics.GetMember(absorbed, "Count"));

                using (var writer = new FastBufferWriter(384, Allocator.Temp))
                {
                    writer.WriteValueSafe(++outgoingSequence);
                    writer.WriteValueSafe(new FixedString128Bytes(survivorId));
                    writer.WriteValueSafe(survivorCount);
                    writer.WriteValueSafe(new FixedString128Bytes(absorbedId));
                    writer.WriteValueSafe(absorbedCount);
                    foreach (ulong clientId in netcode.ConnectedClientsIds)
                    {
                        if (clientId != netcode.LocalClientId)
                        {
                            netcode.CustomMessagingManager.SendNamedMessage(
                                MergeMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                        }
                    }
                }
                mergesSent++;
                Detail("Host merged drop " + Shorten(absorbedId) + " into " + Shorten(survivorId) +
                       "; survivor now " + survivorCount + ", absorbed " + absorbedCount + ".");
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not report a drop merge: " + ex.Message);
            }
        }

        internal static void ReceiveMerge(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsListening || netcode.IsHost ||
                senderClientId != NetworkManager.ServerClientId)
            {
                return;
            }
            try
            {
                int sequence;
                FixedString128Bytes survivorId;
                int survivorCount;
                FixedString128Bytes absorbedId;
                int absorbedCount;
                reader.ReadValueSafe(out sequence);
                reader.ReadValueSafe(out survivorId);
                reader.ReadValueSafe(out survivorCount);
                reader.ReadValueSafe(out absorbedId);
                reader.ReadValueSafe(out absorbedCount);

                applyingRemote = true;
                try
                {
                    object survivor = FindDrop(survivorId.ToString());
                    if (survivor != null)
                    {
                        SetDropCount(survivor, survivorCount);
                    }
                    object absorbed = FindDrop(absorbedId.ToString());
                    if (absorbed != null)
                    {
                        if (absorbedCount <= 0)
                        {
                            completedPickups.Add(absorbedId.ToString());
                            RemoveDrop(absorbed);
                        }
                        else
                        {
                            SetDropCount(absorbed, absorbedCount);
                        }
                    }
                    mergesApplied++;
                }
                finally
                {
                    applyingRemote = false;
                }
            }
            catch (Exception ex)
            {
                unresolved++;
                log.LogWarning("Could not apply a drop merge: " + ex.Message);
            }
        }

        internal static void ReceiveSpawn(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                int sequence;
                FixedString128Bytes uniqueId;
                FixedString128Bytes itemId;
                int count;
                FixedString128Bytes worldId;
                Vector3 position;
                reader.ReadValueSafe(out sequence);
                reader.ReadValueSafe(out uniqueId);
                reader.ReadValueSafe(out itemId);
                reader.ReadValueSafe(out count);
                reader.ReadValueSafe(out worldId);
                reader.ReadValueSafe(out position);
                FixedString128Bytes origin = default;
                if (reader.Position < reader.Length)
                {
                    reader.ReadValueSafe(out origin);
                }
                // 0.65.7 on: the whole item and, for a zombie, the zombie (absent from older peers).
                byte[] whole = ReadBlob(ref reader);
                byte[] zombie = ReadBlob(ref reader);
                // Fork: a drop made again under an id this session already finished.
                bool redrop = false;
                if (reader.Position < reader.Length)
                {
                    reader.ReadValueSafe(out redrop);
                }

                string key = uniqueId.ToString();
                // Not yet in a world (connected in the main menu to copy it): the drop cannot be made here,
                // and marking it as seen made the join snapshot's copy of it be skipped later — a drop
                // the host made while a friend was joining was missing for them (found by `mix`).
                if (!InWorld())
                {
                    return;
                }
                if (redrop)
                {
                    completedPickups.Remove(key);
                    mirrored.Remove(key);
                }
                if (!mirrored.Add(key) || completedPickups.Contains(key))
                {
                    return;
                }
                // The join snapshot re-sends every drop the host has. A joiner that loaded the same
                // save (or a copy of it) already has those drops under the same ids; spawning them
                // again left two of each on the ground.
                if (FindDrop(key) != null)
                {
                    return;
                }
                NetworkManager hostSide = NetworkManager.Singleton;
                if (hostSide != null && hostSide.IsHost && SecondHarvest(origin.ToString(), senderClientId))
                {
                    // Another player finished the same object a moment before: theirs stands, this
                    // one is taken back from everyone (the sender has it on the ground already).
                    completedPickups.Add(key);
                    SendPickupResult(hostSide, ulong.MaxValue, key, itemId.ToString(), 0, 0, false);
                    return;
                }
                SpawnMirrored(key, itemId.ToString(), count, worldId.ToString(), position, whole, zombie);

                // The host is the only route between two clients: a client broadcasts only to the
                // server, so without this a drop one client creates never reaches the other.
                NetworkManager netcode = NetworkManager.Singleton;
                if (netcode != null && netcode.IsHost)
                {
                    using (var writer = new FastBufferWriter(1024 + (whole?.Length ?? 0) + (zombie?.Length ?? 0), Allocator.Temp))
                    {
                        writer.WriteValueSafe(++outgoingSequence);
                        writer.WriteValueSafe(uniqueId);
                        writer.WriteValueSafe(itemId);
                        writer.WriteValueSafe(count);
                        writer.WriteValueSafe(worldId);
                        writer.WriteValueSafe(position);
                        writer.WriteValueSafe(origin);
                        WriteBlob(writer, whole);
                        WriteBlob(writer, zombie);
                        writer.WriteValueSafe(redrop);
                        foreach (ulong clientId in netcode.ConnectedClientsIds)
                        {
                            if (clientId != netcode.LocalClientId && clientId != senderClientId)
                            {
                                netcode.CustomMessagingManager.SendNamedMessage(
                                    SpawnMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                            }
                        }
                    }
                    spawnsSent++;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not read a mirrored drop: " + ex.Message);
            }
        }

        private static bool InWorld()
        {
            object mainGame = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance");
            return mainGame != null && string.Equals(Convert.ToString(CoopDiagnostics.GetMember(mainGame, "gameState")), "InGame", StringComparison.Ordinal);
        }

        /// <summary>At the end of a session, either side: the next world copy is the truth.</summary>
        // ------------------------------------------------------------------ one harvest per object

        // Who harvested which world object, a moment ago (host only): two players finishing the
        // same bed or tree at once each ran its harvest, and both harvests were shared (found by
        // `mix` phase 2d: a ripe bed yielded twice). The first one stands.
        private const float HarvestRaceSeconds = 5f;
        private static readonly Dictionary<string, KeyValuePair<ulong, float>> harvests = new Dictionary<string, KeyValuePair<ulong, float>>();
        private static int harvestRaces;

        /// <summary>The host's own death logic of <paramref name="origin"/>: true when another player harvested it a moment ago.</summary>
        internal static bool HarvestedByAnotherJustNow(string origin)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || string.IsNullOrEmpty(origin) || netcode == null || !netcode.IsListening || !netcode.IsHost)
            {
                return false;
            }
            return SecondHarvest(origin, netcode.LocalClientId);
        }

        /// <summary>Notes who harvested <paramref name="origin"/>; true when someone else did, a moment ago.</summary>
        private static bool SecondHarvest(string origin, ulong harvester)
        {
            if (string.IsNullOrEmpty(origin))
            {
                return false;
            }
            float now = Time.unscaledTime;
            if (harvests.TryGetValue(origin, out KeyValuePair<ulong, float> first) && now - first.Value < HarvestRaceSeconds)
            {
                if (first.Key == harvester)
                {
                    return false;
                }
                if (harvestRaces++ < 6)
                {
                    log.LogInfo("Harvest: " + Shorten(origin) + " was finished by two players at once; the first harvest stands.");
                }
                return true;
            }
            if (harvests.Count > 256)
            {
                harvests.Clear();
            }
            harvests[origin] = new KeyValuePair<ulong, float>(harvester, now);
            return false;
        }

        /// <summary>
        /// A joiner attaching to the host's world: removes the drops of its copy, so that the join
        /// snapshot, which sends every drop the host has (id, count, place), is the whole truth.
        /// </summary>
        internal static void ClearForJoinSnapshot()
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || !InWorld() || (netcode != null && netcode.IsHost))
            {
                return;
            }
            int removed = 0;
            applyingRemote = true;
            try
            {
                object worldData = GetWorldData();
                var scenes = worldData == null ? null : CoopDiagnostics.GetMember(worldData, "gameSceneDataList") as IEnumerable;
                if (scenes == null)
                {
                    return;
                }
                foreach (object scene in scenes)
                {
                    foreach (object drop in ToList(CoopDiagnostics.GetMember(scene, "droppedItems") as IEnumerable))
                    {
                        RemoveDrop(drop);
                        removed++;
                    }
                }
                log.LogInfo("Join: " + removed + " drop(s) of the copied world taken away; the host's join snapshot brings the current ones.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Join: could not clear the copied world's drops (" + removed + " done): " + ex.Message);
            }
            finally
            {
                applyingRemote = false;
            }
        }

        internal static void ResetSession()
        {
            harvests.Clear();
            mirrored.Clear();
            completedPickups.Clear();
            awaitingPickup.Clear();
        }

        private static void SpawnMirrored(string uniqueId, string itemId, int count, string worldId, Vector3 position, byte[] whole = null, byte[] zombie = null)
        {
            applyingRemote = true;
            try
            {
                object item = null;
                if (whole != null)
                {
                    try
                    {
                        // The item itself: a body with its organs (its skulls), a zombie, a filled box.
                        item = CoopGameSerializer.Deserialize(typeof(Item), Decompress(whole));
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("Drop sync: could not read a whole " + itemId + "; made from its id: " + Plugin.Unwrap(ex).Message);
                    }
                }
                if (item == null)
                {
                    item = Activator.CreateInstance(Plugin.FindGameType("Item"), itemId, count);
                }
                ForceItemGuid(item, uniqueId);
                if (zombie != null)
                {
                    AddZombie(uniqueId, zombie);
                }

                object dropSystem = GetDropSystem();
                MethodInfo asDropView = dropSystem == null
                    ? null
                    : AccessTools.Method(dropSystem.GetType(), "DropItemAsDropView");
                if (asDropView == null)
                {
                    unresolved++;
                    return;
                }
                asDropView.Invoke(dropSystem, new[] { item, worldId, (object)position });
                spawnsApplied++;
                if (detailedLogsLeft > 0)
                {
                    detailedLogsLeft--;
                    log.LogInfo("Mirrored drop " + itemId + " x" + count + " in " + worldId + ".");
                }
            }
            catch (Exception ex)
            {
                unresolved++;
                log.LogWarning("Could not mirror a drop: " + ex.Message);
            }
            finally
            {
                applyingRemote = false;
            }
        }

        // ------------------------------------------------------------------ pickup authority

        /// <summary>
        /// Suppresses the original local collection while sharing is active. The old void prefix
        /// announced a pickup after allowing it to credit locally, which cannot resolve a race.
        /// The host now decides whether the item is credited at all.
        /// </summary>
        private static bool CollectDropPrefix(object[] __args)
        {
            if (!Enabled || applyingRemote)
            {
                return true;
            }
            try
            {
                NetworkManager netcode = NetworkManager.Singleton;
                if (!SessionIsLive(netcode))
                {
                    return true;
                }
                string uniqueId = ReadDropGuid(__args != null && __args.Length > 0 ? __args[0] : null);
                if (string.IsNullOrEmpty(uniqueId))
                {
                    return true;
                }
                object view = __args != null && __args.Length > 0 ? __args[0] : null;
                if (netcode.IsHost)
                {
                    if (!TryHostPickup(netcode.LocalClientId, uniqueId, -1))
                    {
                        StopPulling(view);
                    }
                }
                else
                {
                    // The joiner's own bag says how much fits; the host's copy of it can be behind,
                    // and a pickup the host granted beyond it was lost (`mix` phase 2c).
                    int room = LocalRoomFor(view);
                    if (roomNotes++ < 12) log.LogInfo("Drop pickup " + Shorten(uniqueId) + ": the own bag takes " + (room == int.MaxValue ? "all (resource)" : room < 0 ? "an unknown amount" : room.ToString()) + ".");
                    if (room == 0)
                    {
                        if (fullBagNotes++ == 0)
                        {
                            log.LogInfo("Drop pickup: no room in the bag; the drop stays where it is.");
                        }
                        StopPulling(view);
                        return false;
                    }
                    if (view is DropView dropView)
                    {
                        awaitingPickup[uniqueId] = dropView;
                    }
                    SendPickupRequest(netcode, uniqueId, room);
                }
                return false;
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not arbitrate a drop pickup: " + ex.Message);
                // A failure must not fall through to an unsynchronised second credit.
                return false;
            }
        }

        private static int fullBagNotes;
        private static int roomNotes;

        /// <summary>A joiner's drops on their way to its bag, until the host answers.</summary>
        private static readonly Dictionary<string, DropView> awaitingPickup = new Dictionary<string, DropView>();

        /// <summary>
        /// A pickup that was turned down: as the game does for a full bag, the drop stops flying
        /// to the player and lands, and is not pulled in again until the player has walked away.
        /// Without it the drop hung in the air at the player, asking again every physics frame.
        /// </summary>
        private static void StopPulling(object view)
        {
            if (view is DropView dropView && dropView != null && dropView.Data != null)
            {
                dropView.BlockAutoCollect();
                dropView.StopMoving();
            }
        }

        /// <summary>
        /// How much of a drop the local player's own bag takes: all of a resource drop, -1 when it
        /// cannot be told (the host's copy decides then, as before).
        /// </summary>
        private static int LocalRoomFor(object dropOrView)
        {
            try
            {
                object data = CoopDiagnostics.GetMember(dropOrView, "Data") ?? dropOrView;
                if (data == null)
                {
                    return -1;
                }
                if (Convert.ToBoolean(CoopDiagnostics.GetMember(data, "IsResDrop")))
                {
                    return int.MaxValue;
                }
                object item = CoopDiagnostics.GetMember(data, "Item");
                object localPlayer = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerData");
                object inventory = localPlayer == null ? null : CoopDiagnostics.GetMember(localPlayer, "inventory");
                if (item == null || inventory == null)
                {
                    return -1;
                }
                return GetAcceptedCount(inventory, item);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void SendPickupRequest(NetworkManager netcode, string uniqueId, int room)
        {
            using (var writer = new FastBufferWriter(160, Allocator.Temp))
            {
                writer.WriteValueSafe(++outgoingSequence);
                writer.WriteValueSafe(new FixedString128Bytes(uniqueId));
                // Read only by 0.65.3 and later; an older host reads the two fields above.
                writer.WriteValueSafe(room);
                netcode.CustomMessagingManager.SendNamedMessage(
                    PickupRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            pickupRequestsSent++;
        }

        internal static void ReceivePickupRequest(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsListening || !netcode.IsHost || senderClientId == netcode.LocalClientId)
            {
                return;
            }
            try
            {
                int sequence;
                FixedString128Bytes uniqueId;
                reader.ReadValueSafe(out sequence);
                reader.ReadValueSafe(out uniqueId);
                // How much the joiner's own bag takes; absent from a joiner before 0.65.3.
                int room = -1;
                if (reader.Position < reader.Length)
                {
                    reader.ReadValueSafe(out room);
                }
                pickupRequestsReceived++;
                TryHostPickup(senderClientId, uniqueId.ToString(), room);
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not read a drop pickup request: " + ex.Message);
            }
        }

        private static bool TryHostPickup(ulong actorClientId, string uniqueId, int joinerRoom)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (netcode == null || !netcode.IsHost || string.IsNullOrEmpty(uniqueId))
            {
                return false;
            }

            object drop = FindDrop(uniqueId);
            string itemId = drop == null ? string.Empty : Convert.ToString(CoopDiagnostics.GetMember(drop, "Id"));
            int remaining = drop == null ? 0 : Convert.ToInt32(CoopDiagnostics.GetMember(drop, "Count"));
            if (completedPickups.Contains(uniqueId) || drop == null ||
                Convert.ToBoolean(CoopDiagnostics.GetMember(drop, "IsRemoving")))
            {
                pickupRejected++;
                SendPickupResult(netcode, actorClientId, uniqueId, itemId, 0, remaining, false);
                return false;
            }

            object playerData = ResolvePlayerData(actorClientId);
            object inventory = playerData == null ? null : CoopDiagnostics.GetMember(playerData, "inventory");
            object sourceItem = CoopDiagnostics.GetMember(drop, "Item");
            bool isResource = Convert.ToBoolean(CoopDiagnostics.GetMember(drop, "IsResDrop"));
            // A joiner that told how much its own bag takes is believed: the host's copy of that bag
            // can be behind. The copy is still credited, as far as it takes it.
            bool joinerSaid = !isResource && joinerRoom >= 0 && actorClientId != netcode.LocalClientId;
            int accepted = isResource ? remaining
                : joinerSaid ? Math.Min(remaining, joinerRoom)
                : GetAcceptedCount(inventory, sourceItem);
            bool credited = isResource
                ? CreditResource(playerData, itemId, accepted)
                : CreditInventory(inventory, itemId, accepted);
            if (joinerSaid && accepted > 0 && !credited)
            {
                Detail("Host's copy of client " + actorClientId + "'s bag did not take " + itemId + " x" + accepted + "; their own bag does.");
                credited = true;
            }
            if (accepted <= 0 || !credited)
            {
                pickupRejected++;
                SendPickupResult(netcode, actorClientId, uniqueId, itemId, 0, remaining, false);
                Detail("Host rejected pickup " + Shorten(uniqueId) + " for client " + actorClientId + ".");
                return false;
            }

            remaining -= accepted;
            if (remaining <= 0)
            {
                completedPickups.Add(uniqueId);
                RemoveDrop(drop);
                remaining = 0;
            }
            else
            {
                SetDropCount(drop, remaining);
            }
            pickupAccepted++;
            SendPickupResult(netcode, actorClientId, uniqueId, itemId, accepted, remaining, true);
            Detail("Host accepted pickup " + Shorten(uniqueId) + " for client " + actorClientId +
                   ": " + itemId + " x" + accepted + ", remaining=" + remaining + ".");
            return true;
        }

        private static void SendPickupResult(NetworkManager netcode, ulong actorClientId, string uniqueId,
            string itemId, int accepted, int remaining, bool success)
        {
            using (var writer = new FastBufferWriter(384, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString128Bytes(uniqueId ?? string.Empty));
                writer.WriteValueSafe((int)actorClientId);
                writer.WriteValueSafe(success);
                writer.WriteValueSafe(new FixedString128Bytes(itemId ?? string.Empty));
                writer.WriteValueSafe(accepted);
                writer.WriteValueSafe(remaining);
                foreach (ulong clientId in netcode.ConnectedClientsIds)
                {
                    if (clientId != netcode.LocalClientId)
                    {
                        netcode.CustomMessagingManager.SendNamedMessage(
                            PickupResultMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
                    }
                }
            }
        }

        internal static void ReceivePickupResult(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsListening || netcode.IsHost || senderClientId != NetworkManager.ServerClientId)
            {
                return;
            }
            try
            {
                FixedString128Bytes uniqueId;
                int actorClientId;
                bool success;
                FixedString128Bytes itemId;
                int accepted;
                int remaining;
                reader.ReadValueSafe(out uniqueId);
                reader.ReadValueSafe(out actorClientId);
                reader.ReadValueSafe(out success);
                reader.ReadValueSafe(out itemId);
                reader.ReadValueSafe(out accepted);
                reader.ReadValueSafe(out remaining);

                string key = uniqueId.ToString();
                string awardedItemId = itemId.ToString();
                if (actorClientId == (int)netcode.LocalClientId && awaitingPickup.TryGetValue(key, out DropView requested))
                {
                    awaitingPickup.Remove(key);
                    if (!success)
                    {
                        StopPulling(requested);
                    }
                }
                object localPlayerData = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerData");
                bool mine = success && actorClientId == (int)netcode.LocalClientId && accepted > 0;
                // Where the drop lay (it is removed below): what does not fit goes back there.
                object pending = mine ? FindDrop(key) : null;
                if (mine && awardedItemId.StartsWith("game_res_", StringComparison.Ordinal))
                {
                    if (!CreditResource(localPlayerData, awardedItemId, accepted))
                    {
                        unresolved++;
                        log.LogError("Host awarded " + awardedItemId + " x" + accepted + " but the local player state rejected it; state needs resync.");
                    }
                }
                else if (mine)
                {
                    // What the bag no longer takes (it filled up since asking) goes back on the
                    // ground where the player stands, for everyone, instead of being lost.
                    object inventory = CoopDiagnostics.GetMember(localPlayerData, "inventory");
                    int before = GetInventoryItemCount(inventory, awardedItemId);
                    CreditInventory(inventory, awardedItemId, accepted);
                    int left = accepted - Math.Max(0, GetInventoryItemCount(inventory, awardedItemId) - before);
                    if (left > 0)
                    {
                        PutBack(localPlayerData, pending, awardedItemId, left);
                    }
                }

                object drop = FindDrop(key);
                if (drop != null)
                {
                    if (remaining <= 0)
                    {
                        completedPickups.Add(key);
                        RemoveDrop(drop);
                    }
                    else
                    {
                        SetDropCount(drop, remaining);
                    }
                }
                else if (success && remaining > 0)
                {
                    unresolved++;
                }
                pickupResultsApplied++;
                Detail(success
                    ? "Applied host pickup result " + Shorten(key) + ": " + itemId + " x" + accepted + ", remaining=" + remaining + "."
                    : "Host rejected pickup " + Shorten(key) + "; drop remains available.");
            }
            catch (Exception ex)
            {
                unresolved++;
                log.LogWarning("Could not apply a host pickup result: " + ex.Message);
            }
        }

        /// <summary>A picked-up item that did not fit, dropped again at the player's feet (shared as any drop).</summary>
        private static void PutBack(object playerData, object fromDrop, string itemId, int count)
        {
            try
            {
                Type itemType = Plugin.FindGameType("Item");
                object item = Activator.CreateInstance(itemType, itemId, count);
                object dropSystem = GetDropSystem();
                MethodInfo asDropView = dropSystem == null ? null : AccessTools.Method(dropSystem.GetType(), "DropItemAsDropView");
                // Where the drop lay; at the player's feet the drop magnets of whoever stands there
                // took it at once (and a full bag kept trying).
                object at = fromDrop == null ? null : CoopDiagnostics.GetMember(fromDrop, "Position");
                var position = at is Vector3 dropAt ? dropAt : (Vector3)CoopDiagnostics.GetMember(CoopDiagnostics.GetMember(playerData, "position"), "Value");
                string worldId = fromDrop == null ? null : Convert.ToString(CoopDiagnostics.GetMember(fromDrop, "WorldId"));
                if (string.IsNullOrEmpty(worldId)) worldId = Convert.ToString(CoopDiagnostics.GetMember(playerData, "currentGameSceneId"));
                asDropView.Invoke(dropSystem, new[] { item, worldId, (object)position });
                log.LogInfo("Drop pickup: " + itemId + " x" + count + " did not fit in the bag; put back where it lay.");
            }
            catch (Exception ex)
            {
                unresolved++;
                log.LogError("Host awarded " + itemId + " x" + count + " that did not fit in the bag, and it could not be put back on the ground: " + Inner(ex).Message);
            }
        }

        private static Exception Inner(Exception ex)
        {
            return ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
        }

        private static int GetAcceptedCount(object inventory, object sourceItem)
        {
            if (inventory == null || sourceItem == null)
            {
                return 0;
            }
            object data = CoopDiagnostics.GetMember(inventory, "Data");
            MethodInfo capacity = data == null ? null : AccessTools.Method(data.GetType(), "CanAddItemCountToInventory",
                new[] { sourceItem.GetType(), typeof(bool), sourceItem.GetType(), typeof(bool) });
            if (capacity == null)
            {
                return 0;
            }
            return Math.Max(0, Convert.ToInt32(capacity.Invoke(data, new[] { sourceItem, (object)true, null, (object)false })));
        }

        private static bool CreditInventory(object inventory, string itemId, int count)
        {
            if (inventory == null || string.IsNullOrEmpty(itemId) || count <= 0)
            {
                return false;
            }
            try
            {
                Type itemType = Plugin.FindGameType("Item");
                object item = Activator.CreateInstance(itemType, itemId, count);
                MethodInfo add = AccessTools.Method(inventory.GetType(), "AddItemToInventory",
                    new[] { itemType, itemType, typeof(bool) });
                // Some inventory configurations return true from AddItemToInventory even when the
                // source item was left untouched. Treat the observed count change as the commit;
                // otherwise a full inventory can make the authoritative host delete a drop without
                // crediting its owner.
                int before = GetInventoryItemCount(inventory, itemId);
                bool reportedSuccess = add != null && Convert.ToBoolean(add.Invoke(inventory, new[] { item, null, (object)false }));
                int after = GetInventoryItemCount(inventory, itemId);
                return reportedSuccess && after - before == count;
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not credit a pickup: " + ex.Message);
                return false;
            }
        }

        private static int GetInventoryItemCount(object inventory, string itemId)
        {
            object data = inventory == null ? null : CoopDiagnostics.GetMember(inventory, "Data");
            MethodInfo getTotal = null;
            if (data != null)
            {
                foreach (MethodInfo method in data.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name == "GetTotalCountInInventory" && parameters.Length == 3 && parameters[0].ParameterType == typeof(string))
                    {
                        getTotal = method;
                        break;
                    }
                }
            }
            return getTotal == null ? 0 : Math.Max(0, Convert.ToInt32(getTotal.Invoke(data, new object[] { itemId, null, false })));
        }

        private static bool CreditResource(object playerData, string itemId, int count)
        {
            if (playerData == null || string.IsNullOrEmpty(itemId) || count <= 0 ||
                !itemId.StartsWith("game_res_", StringComparison.Ordinal))
            {
                return false;
            }
            try
            {
                string resourceId = itemId.Substring("game_res_".Length);
                MethodInfo get = AccessTools.Method(playerData.GetType(), "GetResInt", new[] { typeof(string) });
                MethodInfo add = AccessTools.Method(playerData.GetType(), "AddRes", new[] { typeof(string), typeof(float) });
                if (get == null || add == null)
                {
                    return false;
                }
                int before = Convert.ToInt32(get.Invoke(playerData, new object[] { resourceId }));
                add.Invoke(playerData, new object[] { resourceId, (float)count });
                int after = Convert.ToInt32(get.Invoke(playerData, new object[] { resourceId }));
                if (after - before != count)
                {
                    // A GameResSystem can clamp its resource (happiness is capped by town quality),
                    // so a short credit is a game rule, not a transport fault.
                    log.LogWarning("Resource " + resourceId + " credit of " + count + " moved it from " +
                                   before + " to " + after + "; the pickup is left in the world.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not credit a resource pickup: " + ex.Message);
                return false;
            }
        }

        /// <summary>The host's own record of a player's data: its own for itself, the saved client record otherwise.</summary>
        internal static object ResolvePlayerDataFor(ulong clientId) => ResolvePlayerData(clientId);

        private static object ResolvePlayerData(ulong clientId)
        {
            object mainGame = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance");
            object save = mainGame == null ? null : CoopDiagnostics.GetMember(mainGame, "GameSave");
            object host = save == null ? null : CoopDiagnostics.GetMember(save, "hostPlayer");
            if (host != null && Convert.ToUInt64(CoopDiagnostics.GetMember(host, "clientId")) == clientId)
            {
                return CoopDiagnostics.GetMember(host, "playerData");
            }
            var clients = save == null ? null : CoopDiagnostics.GetMember(save, "clientPlayers") as IEnumerable;
            if (clients != null)
            {
                foreach (object client in clients)
                {
                    if (client != null && Convert.ToUInt64(CoopDiagnostics.GetMember(client, "clientId")) == clientId)
                    {
                        return CoopDiagnostics.GetMember(client, "playerData");
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Sends every drop currently lying in the world to one client, using the ordinary spawn
        /// message so the receiving side forces the same identity it would for a live drop.
        /// Drops already taken are simply absent, so no removal journal is needed.
        /// </summary>
        internal static int SendSnapshotTo(ulong clientId)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsListening || !netcode.IsHost)
            {
                return 0;
            }
            object worldData = GetWorldData();
            var scenes = worldData == null ? null : CoopDiagnostics.GetMember(worldData, "gameSceneDataList") as IEnumerable;
            if (scenes == null)
            {
                return 0;
            }
            int count = 0;
            foreach (object scene in scenes)
            {
                foreach (object drop in ToList(CoopDiagnostics.GetMember(scene, "droppedItems") as IEnumerable))
                {
                    string uniqueId = ReadDropGuid(drop);
                    object item = CoopDiagnostics.GetMember(drop, "Item");
                    string itemId = item == null ? null : Convert.ToString(CoopDiagnostics.GetMember(item, "id"));
                    string worldId = Convert.ToString(CoopDiagnostics.GetMember(drop, "WorldId"));
                    if (string.IsNullOrEmpty(uniqueId) || string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(worldId))
                    {
                        continue;
                    }
                    object rawPos = CoopDiagnostics.GetMember(drop, "Position");
                    byte[] whole = WholeItem(item);
                    byte[] zombie = ZombieOf(item);
                    using (var writer = new FastBufferWriter(1024 + (whole?.Length ?? 0) + (zombie?.Length ?? 0), Allocator.Temp))
                    {
                        writer.WriteValueSafe(++outgoingSequence);
                        writer.WriteValueSafe(new FixedString128Bytes(uniqueId));
                        writer.WriteValueSafe(new FixedString128Bytes(itemId));
                        writer.WriteValueSafe(Convert.ToInt32(CoopDiagnostics.GetMember(drop, "Count")));
                        writer.WriteValueSafe(new FixedString128Bytes(worldId));
                        writer.WriteValueSafe(rawPos is Vector3 ? (Vector3)rawPos : Vector3.zero);
                        writer.WriteValueSafe(new FixedString128Bytes(string.Empty));
                        WriteBlob(writer, whole);
                        WriteBlob(writer, zombie);
                        netcode.CustomMessagingManager.SendNamedMessage(
                            SpawnMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                    }
                    spawnsSent++;
                    count++;
                }
            }
            return count;
        }

        private static object FindDrop(string uniqueId)
        {
            object worldData = GetWorldData();
            var scenes = worldData == null ? null : CoopDiagnostics.GetMember(worldData, "gameSceneDataList") as IEnumerable;
            if (scenes == null)
            {
                return null;
            }
            foreach (object scene in scenes)
            {
                foreach (object drop in ToList(CoopDiagnostics.GetMember(scene, "droppedItems") as IEnumerable))
                {
                    if (string.Equals(ReadDropGuid(drop), uniqueId, StringComparison.Ordinal))
                    {
                        return drop;
                    }
                }
            }
            return null;
        }

        private static void SetDropCount(object drop, int count)
        {
            object item = CoopDiagnostics.GetMember(drop, "Item");
            PropertyInfo itemCount = item == null ? null : item.GetType().GetProperty("Count",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo notify = drop == null ? null : AccessTools.Method(drop.GetType(), "NotifyCountChanged");
            if (itemCount == null || notify == null)
            {
                throw new MissingMemberException("DropData.Item.Count or NotifyCountChanged");
            }
            itemCount.SetValue(item, count, null);
            notify.Invoke(drop, null);
        }

        private static void RemoveDrop(object drop)
        {
            object dropSystem = GetDropSystem();
            MethodInfo remove = dropSystem == null ? null : AccessTools.Method(dropSystem.GetType(), "RemoveDrop");
            if (remove == null)
            {
                throw new MissingMethodException("DropSystem.RemoveDrop");
            }
            remove.Invoke(dropSystem, new[] { drop, CoopDiagnostics.GetMember(drop, "WorldId") });
        }

        private static string ReadDropGuid(object dropOrView)
        {
            object data = CoopDiagnostics.GetMember(dropOrView, "Data") ?? dropOrView;
            object guid = data == null ? null : CoopDiagnostics.GetMember(data, "UniqueId");
            object id = guid == null ? null : CoopDiagnostics.GetMember(guid, "Id");
            return id == null ? null : id.ToString();
        }

        private static ArrayList ToList(IEnumerable source)
        {
            var list = new ArrayList();
            foreach (object item in source)
            {
                list.Add(item);
            }
            return list;
        }

        // ------------------------------------------------------------------ helpers

        private static bool SessionIsLive(NetworkManager netcode)
        {
            return netcode != null && netcode.IsListening && netcode.IsConnectedClient;
        }

        private static void Broadcast(NetworkManager netcode, string message, FastBufferWriter writer)
        {
            if (netcode.IsHost)
            {
                foreach (ulong clientId in netcode.ConnectedClientsIds)
                {
                    if (clientId != netcode.LocalClientId)
                    {
                        netcode.CustomMessagingManager.SendNamedMessage(
                            message, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                    }
                }
            }
            else
            {
                netcode.CustomMessagingManager.SendNamedMessage(
                    message, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // ------------------------------------------------------------------ whole items

        /// <summary>
        /// Big drops, filled ones and zombies are sent whole: made again from their id, a body
        /// came without its organs (no skulls) and a zombie without the zombie behind it (a white
        /// box that could not be used). Plain small items stay id and count.
        /// </summary>
        internal static byte[] WholeItem(object raw, bool always = false)
        {
            if (!(raw is Item item) || item.Definition == null ||
                (!always && item.Definition.itemSize != ItemSize.Big && item.InventoryCount == 0 && !item.Definition.isLinkedToWgo))
            {
                return null;
            }
            try
            {
                return Compress(CoopGameSerializer.Serialize(item));
            }
            catch (Exception ex)
            {
                log.LogWarning("Drop sync: could not send " + item.id + " whole: " + Plugin.Unwrap(ex).Message);
                return null;
            }
        }

        internal static byte[] ZombieOf(object raw)
        {
            if (!(raw is Item item) || item.Definition == null || !item.Definition.isLinkedToWgo || MainGame.ZombieSystemData == null)
            {
                return null;
            }
            try
            {
                ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(item.UniqueId);
                return zombie == null ? null : Compress(CoopGameSerializer.Serialize(zombie));
            }
            catch (Exception ex)
            {
                log.LogWarning("Drop sync: could not send the zombie of " + item.id + ": " + Plugin.Unwrap(ex).Message);
                return null;
            }
        }

        /// <summary>The zombie of a mirrored zombie drop, into this world's zombie store if it is not there.</summary>
        internal static void AddZombie(string uniqueId, byte[] payload)
        {
            try
            {
                ZombieSystemData system = MainGame.ZombieSystemData;
                if (system == null || !(CoopGameSerializer.Deserialize(typeof(ZombieWgoData), Decompress(payload)) is ZombieWgoData zombie))
                {
                    return;
                }
                if (system.GetZombie(zombie.UniqueId) != null)
                {
                    return;
                }
                system.zombieDrops.Add(zombie);
                system.Cache[zombie.UniqueId.Guid] = zombie;
            }
            catch (Exception ex)
            {
                log.LogWarning("Drop sync: could not add the zombie of drop " + Shorten(uniqueId) + ": " + Plugin.Unwrap(ex).Message);
            }
        }

        private static void WriteBlob(FastBufferWriter writer, byte[] blob)
        {
            writer.WriteValueSafe(blob == null ? 0 : blob.Length);
            if (blob != null && blob.Length > 0)
            {
                writer.WriteBytesSafe(blob, blob.Length);
            }
        }

        private static byte[] ReadBlob(ref FastBufferReader reader)
        {
            if (reader.Position + sizeof(int) > reader.Length)
            {
                return null;
            }
            reader.ReadValueSafe(out int length);
            if (length <= 0 || length > 4 * 1024 * 1024 || reader.Position + length > reader.Length)
            {
                return null;
            }
            byte[] blob = new byte[length];
            reader.ReadBytesSafe(ref blob, length);
            return blob;
        }

        private static byte[] Compress(byte[] raw)
        {
            using (var output = new System.IO.MemoryStream())
            {
                using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }
                return output.ToArray();
            }
        }

        internal static byte[] Decompress(byte[] compressed)
        {
            using (var input = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress))
            using (var output = new System.IO.MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        // ------------------------------------------------------------------ big drops taken

        internal const string TakenMessage = "GK2Coop.DropTaken.v1";

        /// <summary>
        /// A big drop (a body, a zombie, a crate) is taken onto the head through its own
        /// interaction (<c>BigDropInteractionHandler.Interact</c>: the drop removed, the item
        /// carried), not through the collector the pickup sync handles. Nobody else heard of it:
        /// the others kept the drop, could take it too, and the item doubled (Workshop report,
        /// 5 October 2026). Now taking it removes it for everyone.
        /// </summary>
        private static void BigTakePrefix(BigDropInteractionHandler __instance, ref string __state)
        {
            __state = null;
            try
            {
                if (Enabled && !applyingRemote && BigDropField?.GetValue(__instance) is DropView view && view != null && view.Data != null)
                {
                    __state = ReadDropGuid(view);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void BigTakePostfix(bool __result, string __state)
        {
            if (!__result || string.IsNullOrEmpty(__state))
            {
                return;
            }
            NetworkManager netcode = NetworkManager.Singleton;
            if (!SessionIsLive(netcode))
            {
                return;
            }
            completedPickups.Add(__state);
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString128Bytes(__state));
                Broadcast(netcode, TakenMessage, writer);
            }
            bigTakesSent++;
        }

        internal static void ReceiveTaken(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null || !netcode.IsListening)
            {
                return;
            }
            try
            {
                reader.ReadValueSafe(out FixedString128Bytes id);
                string key = id.ToString();
                completedPickups.Add(key);
                object drop = FindDrop(key);
                if (drop != null)
                {
                    applyingRemote = true;
                    try
                    {
                        RemoveDrop(drop);
                    }
                    finally
                    {
                        applyingRemote = false;
                    }
                    bigTakesApplied++;
                }
                if (netcode.IsHost)
                {
                    using (var writer = new FastBufferWriter(256, Allocator.Temp))
                    {
                        writer.WriteValueSafe(id);
                        foreach (ulong clientId in netcode.ConnectedClientsIds)
                        {
                            if (clientId != netcode.LocalClientId && clientId != senderClientId)
                            {
                                netcode.CustomMessagingManager.SendNamedMessage(TakenMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Drop sync: could not apply a big drop taken by " + senderClientId + ": " + ex.Message);
            }
        }

        private static readonly FieldInfo BigDropField = AccessTools.Field(typeof(BigDropInteractionHandler), "drop");
        private static int bigTakesSent;
        private static int bigTakesApplied;

        internal static string ReadItemGuid(object item)
        {
            object guid = CoopDiagnostics.GetMember(item, "UniqueId");
            object id = guid == null ? null : CoopDiagnostics.GetMember(guid, "Id");
            return id == null ? null : id.ToString();
        }

        internal static void ForceItemGuid(object item, string uniqueId)
        {
            object guid = CoopDiagnostics.GetMember(item, "UniqueId");
            if (guid == null)
            {
                return;
            }
            PropertyInfo idProperty = guid.GetType().GetProperty("Id",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (idProperty != null && idProperty.CanWrite)
            {
                idProperty.SetValue(guid, uniqueId, null);
            }
        }

        private static object GetDropSystem()
        {
            object mainGame = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance");
            return mainGame == null ? null : CoopDiagnostics.GetMember(mainGame, "dropSystem");
        }

        private static object GetWorldData()
        {
            object mainGame = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "Instance");
            object save = mainGame == null ? null : CoopDiagnostics.GetMember(mainGame, "GameSave");
            return save == null ? null : CoopDiagnostics.GetMember(save, "worldData");
        }

        private static void Detail(string message)
        {
            if (detailedLogsLeft-- > 0)
            {
                log.LogInfo(message);
            }
        }

        private static string Shorten(string id)
        {
            return string.IsNullOrEmpty(id) ? "?" : (id.Length > 8 ? id.Substring(0, 8) : id);
        }
    }
}
