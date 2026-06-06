using Unity.FPS.Game;
using UnityEngine;

namespace Unity.FPS.AI
{
    [RequireComponent(typeof(EnemyController))]
    public class EnemyMobile : MonoBehaviour
    {
        public enum AIState
        {
            Patrol,
            Follow,
            Attack,
        }

        public Animator Animator;

        [Tooltip("Fraction of the enemy's attack range at which it will stop moving towards target while attacking")]
        [Range(0f, 1f)]
        public float AttackStopDistanceRatio = 0.5f;

        [Tooltip("The random hit damage effects")]
        public ParticleSystem[] RandomHitSparks;

        public ParticleSystem[] OnDetectVfx;
        public PureAudio.PureAudioCue OnDetectSfx;

        [Header("Sound")] public AudioClip MovementSound;
        public MinMaxFloat PitchDistortionMovementSpeed;

        public AIState AiState { get; private set; }
        EnemyController m_EnemyController;
        AudioSource m_AudioSource;

        const string k_AnimMoveSpeedParameter = "MoveSpeed";
        const string k_AnimAttackParameter = "Attack";
        const string k_AnimAlertedParameter = "Alerted";
        const string k_AnimOnDamagedParameter = "OnDamaged";

        void Start()
        {
            m_EnemyController = GetComponent<EnemyController>();
            DebugUtility.HandleErrorIfNullGetComponent<EnemyController, EnemyMobile>(m_EnemyController, this,
                gameObject);

            if (m_EnemyController == null)
                return;

            m_EnemyController.onAttack += OnAttack;
            m_EnemyController.onDetectedTarget += OnDetectedTarget;
            m_EnemyController.onLostTarget += OnLostTarget;
            m_EnemyController.SetPathDestinationToClosestNode();
            m_EnemyController.onDamaged += OnDamaged;

            // Start patrolling
            AiState = AIState.Patrol;

            // adding a audio source to play the movement sound on it
            m_AudioSource = GetComponent<AudioSource>();
            if (m_AudioSource == null)
            {
                m_AudioSource = gameObject.AddComponent<AudioSource>();
            }

            m_AudioSource.clip = MovementSound;
            m_AudioSource.Play();
        }

        void Update()
        {
            if (m_EnemyController == null || m_EnemyController.NavMeshAgent == null)
                return;

            UpdateAiStateTransitions();
            UpdateCurrentAiState();

            float moveSpeed = m_EnemyController.NavMeshAgent.velocity.magnitude;

            // Update animator speed parameter
            if (Animator != null)
            {
                Animator.SetFloat(k_AnimMoveSpeedParameter, moveSpeed);
            }

            // changing the pitch of the movement sound depending on the movement speed
            if (m_AudioSource != null && m_EnemyController.NavMeshAgent.speed > 0f)
            {
                m_AudioSource.pitch = Mathf.Lerp(PitchDistortionMovementSpeed.Min, PitchDistortionMovementSpeed.Max,
                    moveSpeed / m_EnemyController.NavMeshAgent.speed);
            }
        }

        void UpdateAiStateTransitions()
        {
            // Handle transitions 
            switch (AiState)
            {
                case AIState.Follow:
                    // Transition to attack when there is a line of sight to the target
                    if (m_EnemyController.IsSeeingTarget && m_EnemyController.IsTargetInAttackRange)
                    {
                        AiState = AIState.Attack;
                        m_EnemyController.SetNavDestination(transform.position);
                    }

                    break;
                case AIState.Attack:
                    // Transition to follow when no longer a target in attack range
                    if (!m_EnemyController.IsTargetInAttackRange)
                    {
                        AiState = AIState.Follow;
                    }

                    break;
            }
        }

        void UpdateCurrentAiState()
        {
            if (m_EnemyController == null)
                return;

            // Handle logic 
            switch (AiState)
            {
                case AIState.Patrol:
                    m_EnemyController.UpdatePathDestination();
                    m_EnemyController.SetNavDestination(m_EnemyController.GetDestinationOnPath());
                    break;
                case AIState.Follow:
                    if (m_EnemyController.KnownDetectedTarget != null)
                    {
                        Vector3 targetPosition = m_EnemyController.KnownDetectedTarget.transform.position;
                        m_EnemyController.SetNavDestination(targetPosition);
                        m_EnemyController.OrientTowards(targetPosition);
                        m_EnemyController.OrientWeaponsTowards(targetPosition);
                    }
                    break;
                case AIState.Attack:
                    if (m_EnemyController.KnownDetectedTarget != null && m_EnemyController.DetectionModule != null)
                    {
                        Vector3 targetPosition = m_EnemyController.KnownDetectedTarget.transform.position;
                        if (Vector3.Distance(targetPosition,
                                m_EnemyController.DetectionModule.DetectionSourcePosition)
                            >= (AttackStopDistanceRatio * m_EnemyController.DetectionModule.AttackRange))
                        {
                            m_EnemyController.SetNavDestination(targetPosition);
                        }
                        else
                        {
                            m_EnemyController.SetNavDestination(transform.position);
                        }

                        m_EnemyController.OrientTowards(targetPosition);
                        m_EnemyController.TryAtack(targetPosition);
                    }
                    break;
            }
        }

        void OnAttack()
        {
            if (Animator != null)
            {
                Animator.SetTrigger(k_AnimAttackParameter);
            }
        }

        void OnDetectedTarget()
        {
            if (AiState == AIState.Patrol)
            {
                AiState = AIState.Follow;
            }

            if (OnDetectVfx != null)
            {
                for (int i = 0; i < OnDetectVfx.Length; i++)
                {
                    if (OnDetectVfx[i] != null)
                        OnDetectVfx[i].Play();
                }
            }

            if (OnDetectSfx)
            {
                AudioUtility.CreateSFX(OnDetectSfx, transform.position, AudioUtility.AudioGroups.EnemyDetection, 1f);
            }

            if (Animator != null)
            {
                Animator.SetBool(k_AnimAlertedParameter, true);
            }
        }

        void OnLostTarget()
        {
            if (AiState == AIState.Follow || AiState == AIState.Attack)
            {
                AiState = AIState.Patrol;
            }

            if (OnDetectVfx != null)
            {
                for (int i = 0; i < OnDetectVfx.Length; i++)
                {
                    if (OnDetectVfx[i] != null)
                        OnDetectVfx[i].Stop();
                }
            }

            if (Animator != null)
            {
                Animator.SetBool(k_AnimAlertedParameter, false);
            }
        }

        void OnDamaged()
        {
            if (RandomHitSparks != null && RandomHitSparks.Length > 0)
            {
                int n = Random.Range(0, RandomHitSparks.Length);
                if (RandomHitSparks[n] != null)
                    RandomHitSparks[n].Play();
            }

            if (Animator != null)
            {
                Animator.SetTrigger(k_AnimOnDamagedParameter);
            }
        }
    }
}