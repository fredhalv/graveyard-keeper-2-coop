using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace GK2Coop
{
    internal enum CoopRole
    {
        None,
        Host,
        Joiner,
    }

    /// <summary>
    /// One place that knows when a co-op session starts and ends, and what has to be undone when it
    /// ends.
    ///
    /// Before this, each part noticed the end on its own (or not at all), and the game's own static
    /// state outlived the session. Found in play: the host went to the main menu, hosted again in the
    /// same game, and the friend rejoined — the host then saw three keepers, one called "Host" and
    /// one with the host's own Steam name. The first session's connection callbacks were still
    /// attached, and <c>LobbyHelper.connectedClients</c> still held the friend from the first
    /// session, so the new session's bookkeeping ran twice or threw halfway through.
    ///
    /// The role is read every frame from Netcode. A change ends the old session (running every
    /// reset, each on its own so one failing does not stop the rest) and starts the new one with a
    /// new generation number. The resets are idempotent: running one when there is nothing to undo
    /// does nothing.
    /// </summary>
    internal static class CoopLifecycle
    {
        private static ManualLogSource log;
        private static CoopRole role = CoopRole.None;
        private static float startedAt;
        private static int resetFailures;

        internal static CoopRole Role => role;

        /// <summary>Counts sessions in this game process; each start adds one.</summary>
        internal static int Generation { get; private set; }

        internal static int Ended { get; private set; }

        internal static void Init(ManualLogSource source)
        {
            log = source;
        }

        /// <summary>
        /// The game's own session end threw on every shutdown, host and joiner alike. Netcode drops
        /// its <c>CustomMessagingManager</c> before it raises OnServerStopped / OnClientStopped, and
        /// the game's handler (<c>OnlineState.Exit</c> → <c>UnregisterMessageChannels</c>) then
        /// unregisters its channels on the missing manager. The NullReferenceException stopped two
        /// things half way: the game's connection state stayed "hosting" / "connected" (the log
        /// shows "from ConnectedToHostState to ConnectedToHostState" at the next start), and Netcode
        /// skipped the rest of its shutdown (the local client's roles, the prefab list). With the
        /// manager gone its handlers are gone too, so there is nothing to unregister: skip it then.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            try
            {
                Type manager = Plugin.FindGameType("BaseNetworkMessageChannelManager");
                MethodInfo unregister = manager == null ? null : AccessTools.Method(manager, "UnregisterMessageChannels");
                if (unregister == null)
                {
                    log.LogWarning("Lifecycle: the game's UnregisterMessageChannels was not found; its shutdown stays as shipped.");
                    return;
                }
                harmony.Patch(unregister, prefix: new HarmonyMethod(typeof(CoopLifecycle).GetMethod(nameof(UnregisterPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception ex)
            {
                log.LogWarning("Lifecycle: shutdown guard not installed: " + ex.Message);
            }
        }

        private static bool UnregisterPrefix()
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (netcode != null && netcode.CustomMessagingManager != null)
            {
                return true;
            }
            skippedUnregisters++;
            return false;
        }

        private static int skippedUnregisters;

        internal static string Describe()
        {
            return "lifecycle: role=" + role + ", generation=" + Generation + ", ended=" + Ended +
                   ", reset failures=" + resetFailures + ", game shutdowns guarded=" + skippedUnregisters +
                   (role == CoopRole.None ? string.Empty : ", for " + Mathf.RoundToInt(Time.unscaledTime - startedAt) + " s");
        }

        /// <summary>Driven every frame from the plugin.</summary>
        internal static void Tick()
        {
            CoopRole now = ReadRole();
            if (now == role)
            {
                if (role == CoopRole.Host)
                {
                    CoopBuildSync.TickRepairQualityRects(Generation);
                }
                return;
            }
            if (role != CoopRole.None)
            {
                End(role);
            }
            if (now != CoopRole.None)
            {
                Generation++;
                startedAt = Time.unscaledTime;
                log.LogInfo("Session " + Generation + " started as " + now + ".");
            }
            role = now;
        }

        private static CoopRole ReadRole()
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (netcode == null || !netcode.IsListening)
            {
                return CoopRole.None;
            }
            if (netcode.IsHost)
            {
                return CoopRole.Host;
            }
            return netcode.IsClient ? CoopRole.Joiner : CoopRole.None;
        }

        private static void End(CoopRole ended)
        {
            Ended++;
            log.LogInfo("Session " + Generation + " (" + ended + ") ended after " + Mathf.RoundToInt(Time.unscaledTime - startedAt) + " s; resetting.");
            if (ended == CoopRole.Host)
            {
                Run("host connection callbacks", () => Plugin.Instance?.DetachHostCallbacks());
                Run("other players' records and bodies", RemoveRemotePlayers);
                Run("the game's connected-client list", ClearLobbyClients);
                Run("player profiles", CoopPlayerProfiles.ForgetAllClients);
                Run("the session's building changes", CoopBuildSync.ResetJournal);
                Run("the session's zombie changes", CoopZombieSync.ResetJournal);
            }
            if (ended == CoopRole.Joiner)
            {
                Run("the joined world", () => Plugin.Instance?.ForgetJoinedWorld());
            }
            // The bodies live in MainScene, which outlives the world: left standing, the other
            // players' bodies came along into the next world loaded and stood there as statues.
            Run("other players' bodies", () =>
            {
                int destroyed = CoopWatchdog.DestroyOtherBodies();
                if (destroyed > 0)
                {
                    log.LogInfo("Session end: removed " + destroyed + " other player bod(ies).");
                }
            });
            Run("chest bookkeeping", CoopContainerSync.ResetSession);
            Run("drop bookkeeping", CoopDropSync.ResetSession);
            Run("point bookkeeping", CoopTechPointSync.ResetSession);
            Run("carried items", CoopOverheadSync.ResetSession);
            Run("death bookkeeping", CoopWorldSync.ResetSession);
            Run("quest bookkeeping", CoopQuestSync.ResetSession);
            Run("session state", CoopSession.Reset);
            Run("watchdog", CoopWatchdog.Reset);
        }

        private static void Run(string what, Action reset)
        {
            try
            {
                reset();
            }
            catch (Exception ex)
            {
                resetFailures++;
                log.LogWarning("Session end: could not reset " + what + ": " + Plugin.Unwrap(ex).Message);
            }
        }

        /// <summary>
        /// A host who stops hosting but stays in the world would otherwise keep the others' bodies
        /// standing where they were, and their records would be found again by the next session.
        /// </summary>
        private static void RemoveRemotePlayers()
        {
            System.Collections.IList clients = CoopWatchdog.ClientRecords();
            if (clients == null || clients.Count == 0)
            {
                return;
            }
            object local = CoopWatchdog.LocalPlayerData();
            int removed = 0;
            foreach (object record in new List<object>(ToList(clients)))
            {
                object data = CoopDiagnostics.GetMember(record, "playerData");
                if (data == null || ReferenceEquals(data, local))
                {
                    continue;
                }
                CoopWatchdog.DestroyBodiesOf(data);
                clients.Remove(record);
                removed++;
            }
            if (removed > 0)
            {
                log.LogInfo("Session end: removed " + removed + " other player record(s) and their bodies.");
            }
        }

        private static IEnumerable<object> ToList(System.Collections.IList list)
        {
            foreach (object item in list)
            {
                yield return item;
            }
        }

        private static void ClearLobbyClients()
        {
            Type lobbyType = Plugin.FindGameType("LobbyHelper");
            FieldInfo field = lobbyType?.GetField("connectedClients", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is System.Collections.IDictionary clients && clients.Count > 0)
            {
                log.LogInfo("Session end: cleared " + clients.Count + " entr(ies) from the game's connected-client list.");
                clients.Clear();
            }
        }
    }
}
