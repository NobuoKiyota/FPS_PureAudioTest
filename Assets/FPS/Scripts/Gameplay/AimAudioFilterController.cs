using UnityEngine;
using UnityEngine.Audio;

namespace Unity.FPS.Gameplay
{
    [RequireComponent(typeof(PlayerWeaponsManager))]
    public class AimAudioFilterController : MonoBehaviour
    {
        [Header("Audio Mixer Reference")]
        [Tooltip("操作対象の AudioMixer。")]
        public AudioMixer audioMixer;
        
        [Tooltip("MixerのExposed Parameter名。")]
        public string parameterName = "AimCutoff";

        [Header("Filter Settings")]
        [Tooltip("通常時（エイムしていない時）のカットオフ周波数。")]
        public float normalCutoff = 22000f;

        [Tooltip("エイム中のカットオフ周波数。くぐもらせる度合い（低いほどくぐもります）。")]
        public float aimingCutoff = 1200f;

        [Tooltip("フィルターの遷移速度。数値が大きいほど早く変化します。")]
        public float transitionSpeed = 8f;

        private PlayerWeaponsManager m_WeaponsManager;
        private float m_CurrentCutoff;

        void Start()
        {
            m_WeaponsManager = GetComponent<PlayerWeaponsManager>();
            m_CurrentCutoff = normalCutoff;

            if (audioMixer != null)
            {
                audioMixer.SetFloat(parameterName, m_CurrentCutoff);
            }
        }

        void Update()
        {
            if (audioMixer == null || m_WeaponsManager == null) return;

            // エイム中かどうかに応じて目標のカットオフ周波数を決定
            float targetCutoff = m_WeaponsManager.IsAiming ? aimingCutoff : normalCutoff;

            // 現在の値を目標値に滑らかに近づける
            m_CurrentCutoff = Mathf.Lerp(m_CurrentCutoff, targetCutoff, transitionSpeed * Time.deltaTime);

            // AudioMixerのパラメータを更新
            audioMixer.SetFloat(parameterName, m_CurrentCutoff);
        }
    }
}
