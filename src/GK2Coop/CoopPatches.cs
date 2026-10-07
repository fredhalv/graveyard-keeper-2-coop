using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GK2Coop
{
    /// <summary>
    /// Harmony patches that observe the shipped command pipeline and repair the one defect the
    /// static analysis identified in the game's own remote-player initialization.
    /// </summary>
    internal static class CoopPatches
    {
        private static ManualLogSource log;
        private static bool fixRemoteBodyKinematic;
        private static bool matchRemoteBodyScene;
        private static bool fixRemoteBodyAppearance;
        private static bool telemetry;

        internal static void Install(Harmony harmony, ManualLogSource source, bool applyKinematicFix, bool enableTelemetry, bool applySceneFix, bool applyAppearanceFix)
        {
            log = source;
            fixRemoteBodyKinematic = applyKinematicFix;
            matchRemoteBodyScene = applySceneFix;
            fixRemoteBodyAppearance = applyAppearanceFix;
            telemetry = enableTelemetry;

            TryPatch(harmony, "PlayerPhysicalBody", "InitNetworkPlayer", null, nameof(InitNetworkPlayerPostfix));
            TryPatch(harmony, "MainGame", "SpawnPlayer", null, nameof(SpawnPlayerPostfix));
            if (!enableTelemetry)
            {
                return;
            }
            TryPatch(harmony, "CharMoveCommand", "Execute", nameof(CharMoveExecutePrefix), null);
            TryPatch(harmony, "CommandFactory", "SerializeCommand", nameof(SerializeCommandPrefix), null);
            TryPatch(harmony, "CommandFactory", "DeserializeCommand", null, nameof(DeserializeCommandPostfix));
        }

        private static void TryPatch(Harmony harmony, string typeName, string methodName, string prefix, string postfix)
        {
            try
            {
                Type target = Plugin.FindGameType(typeName);
                if (target == null)
                {
                    log.LogWarning("Patch target type not found: " + typeName);
                    return;
                }
                MethodInfo original = AccessTools.Method(target, methodName);
                if (original == null)
                {
                    log.LogWarning("Patch target method not found: " + typeName + "." + methodName);
                    return;
                }
                harmony.Patch(
                    original,
                    prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(CoopPatches), prefix)),
                    postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(CoopPatches), postfix)));
                log.LogInfo("Patched " + typeName + "." + methodName + ".");
            }
            catch (Exception ex)
            {
                log.LogWarning("Failed to patch " + typeName + "." + methodName + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ remote body repair

        /// <summary>
        /// The shipped <c>InitNetworkPlayer</c> calls <c>SetDynamicActive(true)</c>, which leaves the
        /// remote body's rigidbody non-kinematic while disabling the behaviour that would drive it.
        /// The body therefore falls out of the world under gravity, and every replicated
        /// <c>MoveByPosition</c> is rejected with "Trying to move non-static RB by position".
        /// This postfix puts the remote body back into the kinematic state that replication expects.
        /// </summary>
        private static void InitNetworkPlayerPostfix(object __instance, object[] __args)
        {
            try
            {
                object networkPlayer = __args != null && __args.Length > 0 ? __args[0] : null;
                string identity = networkPlayer == null
                    ? "unknown"
                    : "client#" + CoopDiagnostics.GetMember(networkPlayer, "clientId");

                if (fixRemoteBodyKinematic)
                {
                    ForceKinematic(__instance);
                }
                if (matchRemoteBodyScene)
                {
                    MatchLocalPlayerScene(__instance);
                }
                if (fixRemoteBodyAppearance)
                {
                    ApplyStandardAppearance(__instance);
                }

                NeutraliseProxy(__instance, networkPlayer);
                DumpRemoteBodyVisuals(__instance);

                // Prove the player-context swap against a real remote player before any gameplay
                // action is attributed with it.
                CoopPlayerContext.RunSelfTest(networkPlayer);

                var component = __instance as Component;
                var rigidbody = CoopDiagnostics.GetMember(__instance, "rb") as Rigidbody;
                log.LogInfo("Remote body initialized for " + identity +
                            "; kinematic=" + (rigidbody == null ? "no-rb" : rigidbody.isKinematic.ToString()) +
                            "; position=" + (component == null ? "?" : CoopDiagnostics.Format(component.transform.position)) +
                            "; path=" + (component == null ? "?" : CoopDiagnostics.HierarchyPath(component.gameObject)) +
                            "; scene=" + (component == null ? "?" : component.gameObject.scene.name));
            }
            catch (Exception ex)
            {
                log.LogWarning("InitNetworkPlayer postfix failed: " + ex.Message);
            }
        }

        private static void ForceKinematic(object body)
        {
            Type dynamicType = Plugin.FindGameType("PlayerDynamicType");
            MethodInfo setFlag = AccessTools.Method(body.GetType(), "SetNonKinematicFlag");
            if (dynamicType != null && setFlag != null)
            {
                // AND semantics: a single false flag keeps the body kinematic for the rest of its life.
                setFlag.Invoke(body, new[] { Enum.Parse(dynamicType, "ByMovementComponent"), (object)false });
            }

            var rigidbody = CoopDiagnostics.GetMember(body, "rb") as Rigidbody;
            if (rigidbody != null && !rigidbody.isKinematic)
            {
                MethodInfo setDynamic = AccessTools.Method(body.GetType(), "SetDynamicActive");
                if (setDynamic != null)
                {
                    setDynamic.Invoke(body, new object[] { false });
                }
                if (!rigidbody.isKinematic)
                {
                    rigidbody.isKinematic = true;
                }
            }
        }

        /// <summary>
        /// <c>MainGame.SpawnPlayer</c> instantiates into whichever scene is active, which in
        /// practice is the boot scene <c>Logos</c> rather than a gameplay scene. That ties the
        /// remote body's lifetime to a scene the gameplay does not own. Move it alongside the local
        /// player body instead, whatever scene that turns out to be.
        /// </summary>
        private static void MatchLocalPlayerScene(object body)
        {
            var remote = body as Component;
            if (remote == null || remote.transform.parent != null)
            {
                return;
            }

            object localPlayerData = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerData");
            if (localPlayerData == null)
            {
                return;
            }

            foreach (Component candidate in CoopBodies.All())
            {
                if (ReferenceEquals(candidate, remote) ||
                    !ReferenceEquals(CoopDiagnostics.GetMember(candidate, "playerData"), localPlayerData))
                {
                    continue;
                }
                Scene target = candidate.gameObject.scene;
                if (target != remote.gameObject.scene)
                {
                    SceneManager.MoveGameObjectToScene(remote.gameObject, target);
                    log.LogInfo("Moved remote body into the local player's scene: " + target.name + ".");
                }
                return;
            }
        }

        /// <summary>
        /// Off by default, and it should stay that way unless appearance is genuinely being
        /// replicated. The player prefab's own appearance is already correct; the intro chains that
        /// motivated this were an animation state, not a skin, and they resolved once inbound
        /// movement stopped throwing.
        ///
        /// When enabled this mirrors <c>PlayerData.ApplyCustomization</c> against the remote view:
        /// preset *and* palette together. Preset alone is not enough — the parts that
        /// <c>GetPresetForCustomizationData</c> builds use
        /// <c>ColorReplaceType.USE_PALETTE_COLOR_REPLACE</c>, so without the palette the body draws
        /// in raw source colours. It is all-or-nothing for that reason.
        ///
        /// Note that <c>CurrentPreset</c> is an unnamed runtime instance by design
        /// (<c>ScriptableObject.CreateInstance</c>), so an empty name is normal and must not be
        /// treated as invalid.
        /// </summary>
        private static void ApplyStandardAppearance(object body)
        {
            object view = CoopDiagnostics.GetMember(body, "playerView");
            if (view == null)
            {
                return;
            }

            Type helper = Plugin.FindGameType("PlayerSkinHelper");
            object customization = CoopDiagnostics.GetStatic(helper, "playerStandardCustomizationData");
            object preset = CoopDiagnostics.GetStatic(helper, "CurrentPreset");
            if (preset == null && customization != null)
            {
                MethodInfo build = AccessTools.Method(helper, "GetPresetForCustomizationData", new[] { customization.GetType() });
                preset = build == null ? null : build.Invoke(null, new[] { customization });
            }

            object palette = null;
            object affectedPartTypes = null;
            if (customization != null)
            {
                MethodInfo getPalette = AccessTools.Method(helper, "GetColorReplacementPalette", new[] { customization.GetType() });
                object replacePalette = getPalette == null ? null : getPalette.Invoke(null, new[] { customization });
                palette = replacePalette == null ? null : CoopDiagnostics.GetMember(replacePalette, "palette");

                object controller = CoopDiagnostics.GetStatic(Plugin.FindGameType("MainGame"), "PlayerController");
                object characterData = controller == null ? null : CoopDiagnostics.GetMember(controller, "CharacterCustomizationData");
                affectedPartTypes = characterData == null ? null : CoopDiagnostics.GetMember(characterData, "affectedPartTypes");
            }

            MethodInfo setPreset = AccessTools.Method(view.GetType(), "SetPlayerPreset");
            MethodInfo applyColors = AccessTools.Method(view.GetType(), "ApplyPlayerColors",
                new[] { typeof(Texture2D), affectedPartTypes == null ? typeof(object) : affectedPartTypes.GetType(), typeof(bool) });

            if (preset == null || palette == null || affectedPartTypes == null || setPreset == null || applyColors == null)
            {
                log.LogWarning("Remote body appearance left at the prefab default: could not resolve " +
                               (preset == null ? "preset" : palette == null ? "palette" : affectedPartTypes == null ? "part types" : "view methods") +
                               ". Applying a preset without its palette would draw raw source colours.");
                return;
            }

            setPreset.Invoke(view, new[] { preset, (object)false });
            applyColors.Invoke(view, new[] { palette, affectedPartTypes, (object)false });
            log.LogInfo("Applied preset and colour palette to the remote body.");
        }

        /// <summary>
        /// One-shot dump of what a freshly spawned remote body actually draws. Listing the whole
        /// hierarchy proved too coarse — it truncated before reaching anything useful — so this
        /// reports only active, enabled renderers together with the sprite each one is showing.
        /// Remote bodies have been seen still wearing the intro's shackles, and
        /// <c>SkinPresetGK2</c> has no chain part (body, head, arms, beard, hairstyle only), so
        /// whatever draws them should appear here by name.
        /// </summary>
        /// <summary>
        /// A remote body is a puppet: it shows where another player is and nothing more. The prefab
        /// still brings its own drop collectors, drop searcher and interaction component, all of
        /// them active, so a proxy standing near a drop competes with the real player for it and
        /// can consume it. The prefab also idles into its default animation clip, which in this
        /// demo is the chained prisoner pose, so the proxy is nudged to Idle instead.
        /// </summary>
        private static void NeutraliseProxy(object body, object networkPlayer)
        {
            var component = body as Component;
            if (component == null)
            {
                return;
            }

            var disabled = new List<string>();
            foreach (Transform child in component.gameObject.GetComponentsInChildren<Transform>(true))
            {
                switch (child.name)
                {
                    case "DropCollector":
                    case "DropSearcher":
                    case "DropCollision":
                    case "Drop":
                    case "InteractionComponent":
                        if (child.gameObject.activeSelf)
                        {
                            child.gameObject.SetActive(false);
                            disabled.Add(child.name);
                        }
                        break;
                }
            }
            if (disabled.Count > 0)
            {
                log.LogInfo("Neutralised proxy components: " + string.Join(", ", disabled.ToArray()));
            }

            TrySetIdleAnimation(networkPlayer);
        }

        private static void TrySetIdleAnimation(object networkPlayer)
        {
            try
            {
                object playerData = networkPlayer == null ? null : CoopDiagnostics.GetMember(networkPlayer, "playerData");
                object charState = playerData == null ? null : CoopDiagnostics.GetMember(playerData, "charState");
                Type animationState = Plugin.FindGameType("AnimationState");
                if (charState == null || animationState == null)
                {
                    return;
                }
                PropertyInfo value = charState.GetType().GetProperty("Value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (value != null && value.CanWrite)
                {
                    value.SetValue(charState, Enum.Parse(animationState, "Idle"), null);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not set the proxy's idle animation: " + ex.Message);
            }
        }

        private static void DumpRemoteBodyVisuals(object body)
        {
            var component = body as Component;
            if (component == null || dumpedVisuals)
            {
                return;
            }
            dumpedVisuals = true;

            var drawn = new List<string>();
            foreach (Renderer renderer in component.gameObject.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || drawn.Count >= 40)
                {
                    continue;
                }
                var sprite = renderer as SpriteRenderer;
                string detail = renderer.name;
                if (sprite != null && sprite.sprite != null)
                {
                    detail += "[" + sprite.sprite.name + "]";
                }
                drawn.Add(detail);
            }
            log.LogInfo("Remote body drawn renderers (" + drawn.Count + "): " + string.Join(", ", drawn.ToArray()));

            object current = CoopDiagnostics.GetStatic(Plugin.FindGameType("PlayerSkinHelper"), "CurrentPreset");
            object fallback = CoopDiagnostics.GetStatic(Plugin.FindGameType("PlayerSkinHelper"), "DefaultPreset");
            log.LogInfo("Skin presets: current='" + DescribePreset(current) + "'; default='" + DescribePreset(fallback) + "'.");

            var animators = component.gameObject.GetComponentsInChildren<Animator>(true);
            foreach (Animator animator in animators.Take(2))
            {
                if (animator.runtimeAnimatorController == null || animator.layerCount == 0)
                {
                    continue;
                }
                var states = new List<string>();
                for (int layer = 0; layer < animator.layerCount && layer < 8; layer++)
                {
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(layer);
                    states.Add(animator.GetLayerName(layer) + ":w=" + animator.GetLayerWeight(layer).ToString("F2") +
                               ",hash=" + info.shortNameHash);
                }
                log.LogInfo("Remote body animator '" + animator.name + "': " + string.Join(" | ", states.ToArray()));
            }
        }

        private static string DescribePreset(object preset)
        {
            var asset = preset as UnityEngine.Object;
            if (preset == null)
            {
                return "null";
            }
            return asset != null && !string.IsNullOrEmpty(asset.name) ? asset.name : "<unnamed " + preset.GetType().Name + ">";
        }

        private static bool dumpedVisuals;

        private static void SpawnPlayerPostfix(object[] __args)
        {
            try
            {
                object networkPlayer = __args != null && __args.Length > 0 ? __args[0] : null;
                object playerData = networkPlayer == null ? null : CoopDiagnostics.GetMember(networkPlayer, "playerData");
                log.LogInfo("MainGame.SpawnPlayer completed for client#" +
                            (networkPlayer == null ? "?" : Convert.ToString(CoopDiagnostics.GetMember(networkPlayer, "clientId"))) +
                            "; dataHash=" + (playerData == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(playerData)) +
                            "; dataPosition=" + (playerData == null ? "?" : CoopDiagnostics.Format(CoopDiagnostics.ReadPositionValue(playerData))));
                CoopStaleBodyFix.RestoreLocalPlayer(false);
            }
            catch (Exception ex)
            {
                log.LogWarning("SpawnPlayer postfix failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ command telemetry

        /// <summary>
        /// <c>CharMoveCommand.Execute</c> calls <c>GameSave.GetClient(..., considerHost: true)</c>,
        /// which dereferences <c>hostPlayer</c> before testing it. A client receives movement from
        /// the host before it has attached its own player records, so every early command threw
        /// <c>NullReferenceException</c> out of the message handler. Skip the command when its
        /// sender cannot be resolved yet: the next position update supersedes it anyway.
        /// </summary>
        private static bool CharMoveExecutePrefix(object __instance, object[] __args)
        {
            try
            {
                ulong senderClientId = __args != null && __args.Length > 0 ? Convert.ToUInt64(__args[0]) : 0uL;
                object gameSave = __args != null && __args.Length > 1 ? __args[1] : null;
                int playerId = Convert.ToInt32(CoopDiagnostics.GetMember(__instance, "playerId"));
                object rawPosition = CoopDiagnostics.GetMember(__instance, "position");
                Vector3 position = rawPosition is Vector3 ? (Vector3)rawPosition : Vector3.zero;

                object manager = CoopDiagnostics.GetStatic(Plugin.FindGameType("LazyNetwork"), "NetworkManager");
                int myId = manager == null ? -1 : Convert.ToInt32(CoopDiagnostics.GetMember(manager, "MyId"));
                bool applied = playerId != myId;
                // The game applies the move to the command's sender. On a joiner every command comes
                // from the host, including the other joiners' moves the host forwards (CoopHostRelay),
                // so with three or more players every joiner saw the host jump to the other joiners'
                // positions and the other joiners stand still: there, a move whose playerId is not the
                // host's is someone else's. On the host the sender is right (a joiner's own playerId
                // can still be the one from its first, menu-stage connection) and nothing changes.
                Unity.Netcode.NetworkManager netcode = Unity.Netcode.NetworkManager.Singleton;
                bool forwarded = netcode != null && netcode.IsClient && !netcode.IsHost && playerId != (int)senderClientId;
                bool resolved = !applied || CanResolve(gameSave, forwarded ? playerId : (int)senderClientId);

                CoopDiagnostics.RecordMoveCommand(playerId, senderClientId, position, applied, resolved);
                if (applied && !resolved)
                {
                    // Running the original here would throw inside GetClient.
                    return false;
                }
                if (applied && forwarded && __instance is CharMoveCommand move && gameSave is GameSave save &&
                    save.GetClient(playerId, out var mover, considerHost: true) && mover?.playerData != null)
                {
                    // Forwarded by the host: the one who moved, not the host.
                    mover.playerData.position.Value = move.position;
                    mover.playerData.Direction = move.direction;
                    mover.playerData.charState.Value = move.animState;
                    return false;
                }
            }
            catch
            {
                // Telemetry must never block command execution.
            }
            return true;
        }

        /// <summary>
        /// Mirrors <c>GameSave.GetClient(senderClientId, out _, considerHost: true)</c> without
        /// calling it, so a failed lookup does not emit a duplicate engine error.
        /// </summary>
        private static bool CanResolve(object gameSave, int clientId)
        {
            if (gameSave == null)
            {
                return false;
            }
            object hostPlayer = CoopDiagnostics.GetMember(gameSave, "hostPlayer");
            if (hostPlayer != null && Convert.ToInt32(CoopDiagnostics.GetMember(hostPlayer, "clientId")) == clientId)
            {
                return true;
            }
            var clients = CoopDiagnostics.GetMember(gameSave, "clientPlayers") as IEnumerable;
            if (clients == null)
            {
                return false;
            }
            foreach (object client in clients)
            {
                if (Convert.ToInt32(CoopDiagnostics.GetMember(client, "clientId")) == clientId)
                {
                    return true;
                }
            }
            return false;
        }

        private static void SerializeCommandPrefix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] != null)
                {
                    CoopDiagnostics.CountOutgoingCommand(__args[0].GetType().Name);
                }
            }
            catch
            {
            }
        }

        private static void DeserializeCommandPostfix(object __result)
        {
            try
            {
                if (__result != null)
                {
                    CoopDiagnostics.CountIncomingCommand(__result.GetType().Name);
                }
            }
            catch
            {
            }
        }
    }
}
