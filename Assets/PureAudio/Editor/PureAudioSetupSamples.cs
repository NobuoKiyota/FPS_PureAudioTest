using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine.AddressableAssets;

namespace PureAudio.Editor
{
    public static class PureAudioSetupSamples
    {
        [MenuItem("Window/PureAudio/Setup Samples and Register")]
        public static void Setup()
        {
            // 1. まずCSVをアセットにインポートして、CueアセットやCategoryアセットを自動生成
            PureAudioExcelSync.ImportCsvToAssets();

            // 2. Cue名とコピー先音声ファイル名（相対）の対応マッピング
            var clipMapping = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "Weapon_Blaster_Shot", "SE/Blaster_Shot.wav" },
                { "Weapon_Launcher_Charge_BuildUp", "SE/Launcher_Charge_BuildUp.wav" },
                { "Weapon_Launcher_Charge_Loop", "SE/Launcher_Charge_Loop.wav" },
                { "Weapon_Launcher_Release", "SE/Launcher_Release.wav" },
                { "Weapon_Launcher_Explosion", "SE/Launcher_Explosion.wav" },
                { "Weapon_Shotgun_Shot", "SE/Shotgun_Shot.wav" },
                { "Weapon_Cooling", "SE/Weapon_Cooling.wav" },
                { "Footstep_Dirt", "SE/Footstep.wav" },
                { "Player_Jump", "SE/Jump.wav" },
                { "Player_Land", "SE/Land.wav" },
                { "Player_Land_Damage", "SE/Land_Damage.wav" },
                { "Player_Jetpack_Use", "SE/Jetpack_Use.wav" },
                { "Player_Damage_Tick", "SE/Damage_Tick.mp3" },
                { "Player_Damage_Tick2", "SE/Damage_Tick2.mp3" },
                { "Enemy_Hoverbot_Alert", "SE/Hoverbot_Alert.wav" },
                { "Enemy_Hoverbot_Attack", "SE/Hoverbot_Attack.wav" },
                { "Enemy_Hoverbot_Movement", "SE/Hoverbot_Movement.wav" },
                { "Enemy_Hoverbot_Death", "SE/Hoverbot_Death.wav" },
                { "Enemy_Turret_Alert", "SE/Turret_Alert.mp3" },
                { "Enemy_Turret_Attack", "SE/Turret_Attack.wav" },
                { "Enemy_Turret_Death", "SE/Turret_Death.wav" },
                { "Pickup_Weapon_Small", "SE/Pickup_Weapon_Small.wav" },
                { "Pickup_Weapon_Medium", "SE/Pickup_Weapon_Medium.wav" },
                { "Pickup_Jetpack", "SE/Pickup_Jetpack.wav" },
                { "Pickup_Health", "SE/Pickup_Health.wav" },
                { "Notification_Chime", "SE/Notification_Chime.wav" },
                { "Notification_End", "SE/Notification_End.wav" },
                { "Notification_Snap", "SE/Notification_Snap.wav" },
                { "BGM_Main", "BGM/Wind_Ambience.mp3" }
            };

            string clipsRoot = "Assets/PureAudio/AudioClips";
            string cuesFolder = "Assets/PureAudio/Resources/PureAudio/Cues";

            int successCount = 0;

            foreach (var kvp in clipMapping)
            {
                string cueName = kvp.Key;
                string clipSubPath = kvp.Value;

                string cueAssetPath = $"{cuesFolder}/{cueName}.asset";
                string clipAssetPath = $"{clipsRoot}/{clipSubPath}";

                var cue = AssetDatabase.LoadAssetAtPath<PureAudioCue>(cueAssetPath);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipAssetPath);

                if (cue != null)
                {
                    if (clip != null)
                    {
                        if (cue.isAddressable)
                        {
                            var assetRef = RegisterToAddressables(clip);
                            if (assetRef != null)
                            {
                                cue.audioClipReferences = new List<AssetReferenceT<AudioClip>> { assetRef };
                                cue.directAudioClips = new List<AudioClip>(); // 直接参照は空にする
                                Debug.Log($"[PureAudio Setup] Registered (Addressable) '{clipSubPath}' ➔ '{cueName}' cue.");
                            }
                            else
                            {
                                cue.directAudioClips = new List<AudioClip> { clip };
                                cue.audioClipReferences = new List<AssetReferenceT<AudioClip>>();
                                Debug.LogWarning($"[PureAudio Setup] Addressable registration failed, fallback to direct reference for: {cueName}");
                            }
                        }
                        else
                        {
                            cue.directAudioClips = new List<AudioClip> { clip };
                            cue.audioClipReferences = new List<AssetReferenceT<AudioClip>>(); // Addressable参照は空にする
                            Debug.Log($"[PureAudio Setup] Registered (Direct) '{clipSubPath}' ➔ '{cueName}' cue.");
                        }
                        EditorUtility.SetDirty(cue);
                        successCount++;
                    }
                    else
                    {
                        Debug.LogWarning($"[PureAudio Setup] AudioClip not found at: {clipAssetPath}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[PureAudio Setup] Cue asset not found at: {cueAssetPath}");
                }
            }

            // 3. 既存の武器プレハブにPureAudio用Cue名を自動割り当て
            SetupWeaponPrefabCues();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[PureAudio Setup] サンプル登録完了: {successCount} / {clipMapping.Count} 個の音源を自動的に登録しました。");
        }

        private static void SetupWeaponPrefabCues()
        {
            var weapons = new[]
            {
                new { Path = "Assets/FPS/Prefabs/Weapons/Weapon_Blaster.prefab", Shoot = "Weapon_Blaster_Shot", Change = "Pickup_Weapon_Small" },
                new { Path = "Assets/FPS/Prefabs/Weapons/Weapon_Shotgun.prefab", Shoot = "Weapon_Shotgun_Shot", Change = "Pickup_Weapon_Medium" },
                new { Path = "Assets/FPS/Prefabs/Weapons/Weapon_Launcher.prefab", Shoot = "Weapon_Launcher_Release", Change = "Pickup_Weapon_Medium" }
            };

            foreach (var w in weapons)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(w.Path);
                if (prefab != null)
                {
                    var wc = prefab.GetComponent("WeaponController");
                    if (wc != null)
                    {
                        var type = wc.GetType();
                        var shootField = type.GetField("PureAudioShootCue");
                        var changeField = type.GetField("PureAudioChangeCue");

                        if (shootField != null) shootField.SetValue(wc, w.Shoot);
                        if (changeField != null) changeField.SetValue(wc, w.Change);

                        EditorUtility.SetDirty(wc);
                        PrefabUtility.SavePrefabAsset(prefab);
                        Debug.Log($"[PureAudio Setup] Configured weapon prefab cues via Reflection for: {w.Path}");
                    }
                    else
                    {
                        Debug.LogWarning($"[PureAudio Setup] WeaponController component not found on prefab: {w.Path}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[PureAudio Setup] Weapon prefab not found at: {w.Path}");
                }
            }
        }

        private static AssetReferenceT<AudioClip> RegisterToAddressables(AudioClip clip)
        {
            if (clip == null) return null;

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[PureAudio Setup] AddressableAssetSettings not found. Please initialize Addressables first via Window -> Asset Management -> Addressables -> Groups.");
                return null;
            }

            string path = AssetDatabase.GetAssetPath(clip);
            string guid = AssetDatabase.AssetPathToGUID(path);

            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning($"[PureAudio Setup] Could not get GUID for asset: {path}");
                return null;
            }

            var group = settings.DefaultGroup;
            var entry = settings.CreateOrMoveEntry(guid, group);
            if (entry != null)
            {
                entry.address = path; // アドレス名をパスに設定
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);
            }

            return new AssetReferenceT<AudioClip>(guid);
        }
    }
}
