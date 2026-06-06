using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using PureAudio;

namespace Unity.FPS.Game
{
    [DisallowMultipleComponent]
    public class PureAudioBGMController : MonoBehaviour
    {
        [Header("Legacy Audio")]
        [Tooltip("Disable and stop any existing legacy AudioSources on this object or its children.")]
        public bool disableLegacyAudioSources = true;

        [Tooltip("Additional AudioSources to stop if not found on this object hierarchy.")]
        public AudioSource[] legacyAudioSources = Array.Empty<AudioSource>();

        [Header("Persistence")]
        [Tooltip("Keep this object alive across scene loads.")]
        public bool dontDestroyOnLoad = true;

        [Header("Default BGM")]
        public PureAudioCue defaultBgmCue;
        public float defaultFadeTime = 1f;
        [Range(0f, 1f)]
        public float defaultVolume = 1f;
        public bool playOnStart = true;

        [Header("Scene BGM Mapping")]
        public SceneBgmEntry[] sceneBgmEntries = Array.Empty<SceneBgmEntry>();

        [Serializable]
        public class SceneBgmEntry
        {
            [Tooltip("Scene name to match when loading.")]
            public string sceneName;
            [Tooltip("PureAudio cue to play for this scene.")]
            public PureAudioCue bgmCue;
            [Tooltip("Fade time when switching to this scene BGM.")]
            public float fadeTime = 1f;
            [Tooltip("Volume multiplier for this scene BGM.")]
            [Range(0f, 1f)]
            public float volumeScale = 1f;
        }

        private static PureAudioBGMController s_Instance;

        void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_Instance = this;

            if (dontDestroyOnLoad)
            {
                DontDestroyOnLoad(gameObject);
            }

            if (disableLegacyAudioSources)
            {
                StopLegacyAudioSources();
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void Start()
        {
            if (playOnStart)
            {
                PlayBgmForScene(SceneManager.GetActiveScene().name);
            }
        }

        void OnDestroy()
        {
            if (s_Instance == this)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                s_Instance = null;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            PlayBgmForScene(scene.name);
        }

        public void PlayBgmForScene(string sceneName)
        {
            if (PureAudioEngine.Instance == null)
            {
                Debug.LogWarning("PureAudioBGMController: PureAudioEngine is not available.");
                return;
            }

            var entry = sceneBgmEntries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.sceneName) &&
                string.Equals(e.sceneName, sceneName, StringComparison.OrdinalIgnoreCase));

            if (entry != null && entry.bgmCue != null)
            {
                PureAudioEngine.Instance.PlayBGM(entry.bgmCue, Mathf.Max(0f, entry.fadeTime), Mathf.Clamp01(entry.volumeScale));
                Debug.Log($"PureAudioBGMController: Playing scene BGM '{entry.bgmCue.cueName}' for scene '{sceneName}'");
                return;
            }

            if (defaultBgmCue != null)
            {
                PureAudioEngine.Instance.PlayBGM(defaultBgmCue, Mathf.Max(0f, defaultFadeTime), Mathf.Clamp01(defaultVolume));
                Debug.Log($"PureAudioBGMController: Playing default BGM '{defaultBgmCue.cueName}' for scene '{sceneName}'");
            }
            else
            {
                Debug.LogWarning($"PureAudioBGMController: No BGM cue configured for scene '{sceneName}' and no default BGM cue set.");
            }
        }

        public void StopBgm(float fadeTime = 1f)
        {
            if (PureAudioEngine.Instance == null)
                return;

            var category = PureAudioEngine.Instance.categories?.Find(c => c != null &&
                c.categoryName.Equals("BGM", StringComparison.OrdinalIgnoreCase));

            if (category != null)
            {
                PureAudioEngine.Instance.StopCategory(category, Mathf.Max(0f, fadeTime));
            }
        }

        private void StopLegacyAudioSources()
        {
            var sources = GetComponentsInChildren<AudioSource>(true);
            foreach (var source in sources)
            {
                if (source == null) continue;
                source.Stop();
                source.playOnAwake = false;
                source.enabled = false;
            }

            foreach (var source in legacyAudioSources)
            {
                if (source == null) continue;
                source.Stop();
                source.playOnAwake = false;
                source.enabled = false;
            }
        }
    }
}
