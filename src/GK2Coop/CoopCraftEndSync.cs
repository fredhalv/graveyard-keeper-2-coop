using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace GK2Coop
{
    /// <summary>
    /// World-changing crafts finish for everyone.
    ///
    /// Repairs, cleared blockages, opened quest doors and grown crops are crafts whose recipe
    /// replaces or removes the object when it finishes (<c>CraftDef.replaceWgoId</c>). They run on
    /// each machine, so a blockage one player cleared stayed on the other player's screen, and a
    /// repair one player made was never made for the other.
    ///
    /// Every craft ends in <c>WgoData.OnCraftEnd</c>. When a replacing craft ends on one machine,
    /// that machine announces the object, what it was, and the recipe; the host passes it on. A
    /// receiving machine ends the same recipe on its copy — the same replacement, the same end
    /// scripts — unless its copy has already changed (it finished first, or the object is gone),
    /// so no machine finishes one craft twice. Host-run production stations are excluded: they are
    /// mirrored by <see cref="CoopCraftSync"/>.
    /// </summary>
    internal static class CoopCraftEndSync
    {
        internal const string EndMessage = "GK2Coop.CraftEnd.v1";

        private static ManualLogSource log;
        private static bool applying;
        private static int sent;
        private static int applied;
        private static int alreadyDone;
        private static int logsLeft = 12;

        private sealed class Pending
        {
            internal string WgoId;
            internal string ObjectType;
            internal string CraftId;
        }

        [ThreadStatic] private static Pending pending;

        internal static bool Enabled { get; set; } = true;

        internal static string Describe()
        {
            return $"craft end sync: sent={sent}, applied={applied}, already done={alreadyDone}";
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
                MethodInfo onCraftEnd = AccessTools.Method(Plugin.FindGameType("WgoData"), "OnCraftEnd", new[] { Plugin.FindGameType("CraftElementBase") });
                if (onCraftEnd == null)
                {
                    log.LogWarning("Craft end sync: WgoData.OnCraftEnd not found; world-changing crafts stay local.");
                    Enabled = false;
                    return;
                }
                harmony.Patch(onCraftEnd,
                    prefix: new HarmonyMethod(typeof(CoopCraftEndSync).GetMethod(nameof(OnCraftEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(CoopCraftEndSync).GetMethod(nameof(OnCraftEndPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                log.LogInfo("Craft end sync: repairs, cleared blockages and other world-changing crafts finish for everyone.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                log.LogWarning("Craft end sync disabled: " + ex.Message);
            }
        }

        /// <summary>Captured before the object is replaced, while it still has its old type.</summary>
        private static void OnCraftEndPrefix(object __instance, object ce)
        {
            pending = null;
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || applying || ce == null || netcode == null || !netcode.IsListening)
            {
                return;
            }
            object def = CoopDiagnostics.GetMember(ce, "Def");
            if (def == null || def.GetType().Name != "CraftDef" || string.IsNullOrEmpty(Convert.ToString(CoopDiagnostics.GetMember(def, "replaceWgoId"))))
            {
                return;
            }
            object component = CoopDiagnostics.GetMember(__instance, "CraftComponent");
            // A finished dismantle removes the object, which the host's station state cannot carry.
            if (component != null && CoopCraftSync.IsShared(component) && !Convert.ToBoolean(CoopDiagnostics.GetMember(def, "isObjDestroyCraft")))
            {
                return;
            }
            pending = new Pending
            {
                WgoId = WgoId(__instance),
                ObjectType = Convert.ToString(CoopDiagnostics.GetMember(__instance, "id")),
                CraftId = Convert.ToString(CoopDiagnostics.GetMember(ce, "CraftId"))
            };
        }

        private static void OnCraftEndPostfix()
        {
            Pending done = pending;
            pending = null;
            NetworkManager netcode = NetworkManager.Singleton;
            if (done == null || netcode == null || !netcode.IsListening)
            {
                return;
            }
            try
            {
                Send(netcode, null, done);
                sent++;
                if (netcode.IsHost)
                {
                    // What the craft left in the object (a garden's harvest) is rolled on each
                    // machine; the host's roll is the one everyone keeps.
                    CoopContainerSync.PublishNow(done.WgoId);
                }
                if (logsLeft > 0)
                {
                    logsLeft--;
                    log.LogInfo($"Craft end sync: {done.CraftId} finished on {done.ObjectType} {Short(done.WgoId)}; telling the other players.");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft end sync: could not announce a finished craft: " + ex.Message);
            }
        }

        private static void Send(NetworkManager netcode, ulong? except, Pending done)
        {
            using (var writer = new FastBufferWriter(384, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString64Bytes(done.WgoId));
                writer.WriteValueSafe(new FixedString128Bytes(done.ObjectType));
                writer.WriteValueSafe(new FixedString128Bytes(done.CraftId));
                if (netcode.IsHost)
                {
                    foreach (ulong clientId in netcode.ConnectedClientsIds)
                    {
                        if (clientId != netcode.LocalClientId && clientId != except)
                        {
                            netcode.CustomMessagingManager.SendNamedMessage(EndMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
                        }
                    }
                }
                else
                {
                    netcode.CustomMessagingManager.SendNamedMessage(EndMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
                }
            }
        }

        internal static void Receive(ulong sender, FastBufferReader reader)
        {
            NetworkManager netcode = NetworkManager.Singleton;
            if (!Enabled || netcode == null)
            {
                return;
            }
            try
            {
                reader.ReadValueSafe(out FixedString64Bytes wgoText);
                reader.ReadValueSafe(out FixedString128Bytes typeText);
                reader.ReadValueSafe(out FixedString128Bytes craftText);
                var done = new Pending { WgoId = wgoText.ToString(), ObjectType = typeText.ToString(), CraftId = craftText.ToString() };
                if (netcode.IsHost)
                {
                    Send(netcode, sender, done);
                }
                Apply(done);
                if (netcode.IsHost)
                {
                    // Also when the host had already finished it: the joiner rolled its own contents.
                    CoopContainerSync.PublishNow(done.WgoId);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Craft end sync: could not apply a finished craft from " + sender + ": " + Inner(ex).Message);
            }
        }

        private static void Apply(Pending done)
        {
            object wgo = FindWgo(done.WgoId);
            if (wgo == null || Convert.ToString(CoopDiagnostics.GetMember(wgo, "id")) != done.ObjectType)
            {
                // Gone, or already turned into what the other machine made of it.
                alreadyDone++;
                return;
            }
            object component = CoopDiagnostics.GetMember(wgo, "CraftComponent");
            Type paramsType = Plugin.FindGameType("CraftParamsData");
            object parameters = Activator.CreateInstance(paramsType, done.CraftId, wgo, Enum.ToObject(paramsType.GetNestedType("CraftParamsType"), 0), -1);
            object noRequirements = Activator.CreateInstance(typeof(List<>).MakeGenericType(Plugin.FindGameType("NeedItemData")));
            object element = Activator.CreateInstance(Plugin.FindGameType("CraftElement"), done.CraftId, 1, noRequirements, parameters);
            // What the game prepares before it finishes a craft.
            element.GetType().GetMethod("BindCraftable").Invoke(element, new[] { wgo });
            element.GetType().GetMethod("UpdateActualOutputBeforeFinish").Invoke(element, null);
            applying = true;
            try
            {
                component?.GetType().GetMethod("Clear", Type.EmptyTypes).Invoke(component, null);
                wgo.GetType().GetMethod("OnCraftEnd", new[] { Plugin.FindGameType("CraftElementBase") }).Invoke(wgo, new[] { element });
                applied++;
                if (logsLeft > 0)
                {
                    logsLeft--;
                    log.LogInfo($"Craft end sync: finished {done.CraftId} on {done.ObjectType} {Short(done.WgoId)} as another player did.");
                }
            }
            finally
            {
                applying = false;
            }
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
