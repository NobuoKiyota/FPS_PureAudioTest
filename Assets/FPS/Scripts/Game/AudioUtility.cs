using UnityEngine;
using UnityEngine.Audio;

namespace Unity.FPS.Game
{
    public class AudioUtility
    {
        static AudioManager s_AudioManager;

        public enum AudioGroups
        {
            DamageTick,
            Impact,
            EnemyDetection,
            Pickup,
            WeaponShoot,
            WeaponOverheat,
            WeaponChargeBuildup,
            WeaponChargeLoop,
            HUDVictory,
            HUDObjective,
            EnemyAttack
        }

        public static void CreateSFX(PureAudio.PureAudioCue cue, Vector3 position, AudioGroups audioGroup, float spatialBlend,
            float rolloffDistanceMin = 1f)
        {
            if (cue == null) return;

            if (PureAudio.PureAudioEngine.Instance != null)
            {
                var voice = PureAudio.PureAudioEngine.Instance.PlaySE3D(cue, position, 1f);
                if (voice != null)
                {
                    var source = voice.GetComponent<AudioSource>();
                    if (source != null)
                    {
                        source.minDistance = rolloffDistanceMin;

                        var mixerGroup = GetAudioGroup(audioGroup);
                        if (mixerGroup != null)
                        {
                            source.outputAudioMixerGroup = mixerGroup;
                        }
                    }
                }
            }
            else
            {
                // フォールバック (PureAudioEngine がシーン内に配置されていない場合)
                if (cue.directAudioClips != null && cue.directAudioClips.Count > 0)
                {
                    var clip = cue.directAudioClips[0];
                    if (clip != null)
                    {
                        CreateSFX(clip, position, audioGroup, spatialBlend, rolloffDistanceMin);
                    }
                }
            }
        }


        public static void CreateSFX(AudioClip clip, Vector3 position, AudioGroups audioGroup, float spatialBlend,
            float rolloffDistanceMin = 1f)
        {
            if (clip == null) return;

            if (PureAudio.PureAudioEngine.Instance != null)
            {
                // 音声カテゴリの決定
                string categoryName = "SE";
                if (audioGroup == AudioGroups.HUDVictory || audioGroup == AudioGroups.HUDObjective || audioGroup == AudioGroups.DamageTick)
                {
                    categoryName = "UI";
                }

                // PureAudioを使用して3D音源を再生 (動的テンポラリCue対応)
                var voice = PureAudio.PureAudioEngine.Instance.PlaySE3D(clip, position, 1f, categoryName);
                if (voice != null)
                {
                    var source = voice.GetComponent<AudioSource>();
                    if (source != null)
                    {
                        source.spatialBlend = spatialBlend;
                        source.minDistance = rolloffDistanceMin;

                        // 必要に応じてAudioMixerGroupを紐付け
                        var mixerGroup = GetAudioGroup(audioGroup);
                        if (mixerGroup != null)
                        {
                            source.outputAudioMixerGroup = mixerGroup;
                        }
                    }
                }
            }
            else
            {
                // フォールバック (PureAudioEngine がシーン内に配置されていない場合)
                GameObject impactSfxInstance = new GameObject();
                impactSfxInstance.transform.position = position;
                AudioSource source = impactSfxInstance.AddComponent<AudioSource>();
                source.clip = clip;
                source.spatialBlend = spatialBlend;
                source.minDistance = rolloffDistanceMin;
                source.Play();

                source.outputAudioMixerGroup = GetAudioGroup(audioGroup);

                TimedSelfDestruct timedSelfDestruct = impactSfxInstance.AddComponent<TimedSelfDestruct>();
                timedSelfDestruct.LifeTime = clip.length;
            }
        }

        public static AudioMixerGroup GetAudioGroup(AudioGroups group)
        {
            if (s_AudioManager == null)
                s_AudioManager = Object.FindFirstObjectByType<AudioManager>();

            if (s_AudioManager == null)
            {
                Debug.LogWarning("AudioManager not found in scene. Audio mixer groups unavailable.");
                return null;
            }

            var groups = s_AudioManager.FindMatchingGroups(group.ToString());

            if (groups != null && groups.Length > 0)
                return groups[0];

            Debug.LogWarning("Didn't find audio group for " + group.ToString());
            return null;
        }

        public static void SetMasterVolume(float value)
        {
            if (s_AudioManager == null)
                s_AudioManager = Object.FindFirstObjectByType<AudioManager>();

            if (s_AudioManager == null)
            {
                Debug.LogWarning("AudioManager not found in scene. Master volume control unavailable.");
                return;
            }

            if (value <= 0)
                value = 0.001f;
            float valueInDb = Mathf.Log10(value) * 20;

            s_AudioManager.SetFloat("MasterVolume", valueInDb);
        }

        public static float GetMasterVolume()
        {
            if (s_AudioManager == null)
                s_AudioManager = Object.FindFirstObjectByType<AudioManager>();

            if (s_AudioManager == null)
            {
                Debug.LogWarning("AudioManager not found in scene. Returning default master volume.");
                return 1f;
            }

            s_AudioManager.GetFloat("MasterVolume", out var valueInDb);
            return Mathf.Pow(10f, valueInDb / 20.0f);
        }
    }
}
