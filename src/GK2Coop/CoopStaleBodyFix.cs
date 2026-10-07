using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GK2Coop
{
    /// <summary>
    /// A departed player's body must not break the players who stay.
    ///
    /// Every player body's <c>PlayerInteractionComponent.Init</c> subscribes to the local player's
    /// <c>PlayerController.OnControlStateChanged</c> and to two static world events
    /// (<c>GameSceneData.OnWgoDataOnScenePreRemove</c>, <c>OnDropOnScenePreRemoved</c>) and never
    /// unsubscribes. When a remote player's body is destroyed — every disconnect, and every
    /// "copy host world and join", which connects once at the menu and again in the world — the
    /// dead component stays subscribed. The next control change (sleeping, opening any window)
    /// then threw a NullReferenceException on its destroyed collider, and the exception stopped the
    /// event before the remaining handlers ran: the host could no longer sleep once a joiner had
    /// come and gone.
    ///
    /// Each handler now checks whether its component still exists; a destroyed one unsubscribes
    /// itself and is skipped.
    /// </summary>
    internal static class CoopStaleBodyFix
    {
        private static ManualLogSource log;
        private static int removed;

        internal static void Install(Harmony harmony, ManualLogSource source)
        {
            log = source;
            try
            {
                Type component = Plugin.FindGameType("PlayerInteractionComponent");
                if (component == null)
                {
                    log.LogWarning("Stale body fix: PlayerInteractionComponent not found.");
                    return;
                }
                var guard = new HarmonyMethod(typeof(CoopStaleBodyFix).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                int patched = 0;
                foreach (string name in new[] { "OnPlayerControlStateChanged", "HandleWgoDataRemoved", "HandeDropRemove" })
                {
                    MethodInfo handler = AccessTools.Method(component, name);
                    if (handler != null)
                    {
                        harmony.Patch(handler, prefix: guard);
                        patched++;
                    }
                }
                // NetworkPlayer.SubscribeToPlayerDataChanges ties each body to its record's position
                // and never lets go: after a session the record lives on and moved a destroyed body
                // (NullReferenceException in Rigidbody.isKinematic, seen on every rejoin from the menu).
                MethodInfo move = AccessTools.Method(Plugin.FindGameType("PlayerPhysicalBody"), "MoveByPosition");
                if (move != null)
                {
                    harmony.Patch(move, prefix: new HarmonyMethod(typeof(CoopStaleBodyFix).GetMethod(nameof(MovePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                }
                // The same record also turns the body (Direction) and sets its animation (charState):
                // on a destroyed body that threw in AnimationComponentBase.ResolveAnimator (seen in
                // `mix3` when a joiner connected from the menu).
                Type animation = Plugin.FindGameType("AnimationComponentBase");
                if (animation != null)
                {
                    var animationGuard = new HarmonyMethod(typeof(CoopStaleBodyFix).GetMethod(nameof(AnimationPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                    foreach (MethodInfo method in AccessTools.GetDeclaredMethods(animation))
                    {
                        if ((method.Name == "SetDirection" || method.Name == "SetState") && !method.IsAbstract && !method.IsStatic)
                        {
                            harmony.Patch(method, prefix: animationGuard);
                            patched++;
                        }
                    }
                }
                log.LogInfo("Stale body fix: guarding " + patched + " event handlers of departed players' bodies.");
            }
            catch (Exception ex)
            {
                log.LogWarning("Stale body fix not installed: " + ex.Message);
            }
        }

        private static int movesSkipped;

        /// <summary>A destroyed body is not moved (its record may still announce positions).</summary>
        private static bool MovePrefix(object __instance)
        {
            if (__instance is UnityEngine.Object body && body != null)
            {
                return true;
            }
            if (movesSkipped++ == 0)
            {
                log.LogInfo("Stale body fix: a departed player's body was still being moved by its old record; ignored.");
            }
            return false;
        }

        private static int animationsSkipped;

        /// <summary>A destroyed body is not turned or animated either.</summary>
        private static bool AnimationPrefix(object __instance)
        {
            if (__instance is UnityEngine.Object component && component != null)
            {
                return true;
            }
            if (animationsSkipped++ == 0)
            {
                log.LogInfo("Stale body fix: a departed player's body was still being turned or animated by its old record; ignored.");
            }
            return false;
        }

        private static int remoteControlChangesSkipped;

        private static bool Prefix(object __instance, MethodBase __originalMethod)
        {
            if (__instance is UnityEngine.Object unityObject && unityObject != null)
            {
                // A live remote body hears the local player's control changes too, and resetting
                // its interaction ran its overlap check where it stands (often on the local
                // player): it entered and left the same objects and overwrote their one shared
                // interactor, so the local interact key could stop working.
                if (__originalMethod.Name == "OnPlayerControlStateChanged" && !IsLocal(__instance))
                {
                    if (remoteControlChangesSkipped++ == 0)
                    {
                        log.LogInfo("Stale body fix: another player's body no longer reacts to the local player's control changes.");
                    }
                    return false;
                }
                return true;
            }
            try
            {
                Unsubscribe(__instance);
                removed++;
                log.LogInfo("Stale body fix: a departed player's body was still listening to " + __originalMethod.Name + "; removed its handlers (" + removed + " so far).");
            }
            catch (Exception ex)
            {
                log.LogWarning("Stale body fix: could not unsubscribe a departed body: " + ex.Message);
            }
            return false;
        }

        private static bool IsLocal(object component)
        {
            PlayerController local = MainGame.Instance == null ? null : MainGame.PlayerController;
            return local == null || ReferenceEquals(local.PlayerInteractionComponent, component);
        }

        /// <summary>
        /// After another player's body is made or taken away: every body's view points the game's
        /// listener (<c>Microphone</c>) at itself when it is set up, so sound followed the other
        /// player and was left on a destroyed body. The listener goes back to the local player,
        /// and, when a body left, the local interaction is looked up again.
        /// </summary>
        internal static void RestoreLocalPlayer(bool resetInteraction)
        {
            try
            {
                PlayerController local = MainGame.Instance == null ? null : MainGame.PlayerController;
                PlayerView view = local == null || local.PhysicalBody == null ? null : local.PhysicalBody.PlayerView;
                if (view == null)
                {
                    return;
                }
                LazySingleton<global::Microphone>.Instance.SetTarget(view.transform);
                if (resetInteraction)
                {
                    local.PlayerInteractionComponent?.ResetInteractionState();
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Stale body fix: could not restore the local player: " + ex.Message);
            }
        }

        private static void Unsubscribe(object component)
        {
            Type type = component.GetType();
            object controller = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerController");
            RemoveHandler(controller?.GetType().GetEvent("OnControlStateChanged"), controller, component, type, "OnPlayerControlStateChanged");
            Type sceneData = Plugin.FindGameType("GameSceneData");
            RemoveHandler(sceneData?.GetEvent("OnWgoDataOnScenePreRemove", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), null, component, type, "HandleWgoDataRemoved");
            RemoveHandler(sceneData?.GetEvent("OnDropOnScenePreRemoved", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), null, component, type, "HandeDropRemove");
        }

        private static void RemoveHandler(EventInfo evt, object target, object component, Type componentType, string method)
        {
            MethodInfo handler = AccessTools.Method(componentType, method);
            if (evt == null || handler == null)
            {
                return;
            }
            Delegate callback = Delegate.CreateDelegate(evt.EventHandlerType, component, handler, false);
            if (callback != null)
            {
                evt.GetRemoveMethod(true)?.Invoke(target, new object[] { callback });
            }
        }
    }
}
