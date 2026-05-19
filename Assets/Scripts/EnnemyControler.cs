using UnityEngine;
using UnityEngine.AI;

public class EnnemyControler : MonoBehaviour
{
    [Header("Stats")]
    [SerializeField] private int damage = 25;
    [SerializeField] private float attackRange = 2f;
    [SerializeField] private float detectionRange = 10f;

    [Header("Mouvement Aléatoire")]
    [SerializeField] private float wanderRadius = 8f;
    [SerializeField] private float wanderTimer = 4f;

    private NavMeshAgent m_Agent;
    private Animator m_Animator;
    private Transform m_Player;
    [SerializeField] private float m_Timer;

    void Start()
    {
        m_Agent = GetComponent<NavMeshAgent>();
        m_Animator = GetComponent<Animator>();

        GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj != null) m_Player = playerObj.transform;

        m_Timer = wanderTimer;
    }

    void Update()
    {
        if (m_Player == null) return;

        float distanceToPlayer = Vector3.Distance(transform.position, m_Player.position);

        if (distanceToPlayer <= attackRange)
        {
            ChasePlayer();
        }
        else
        {
            Wander();
        }

        if (m_Animator != null)
        {
            m_Animator.SetBool("isWalking", m_Agent.velocity.magnitude > 0.1f);
        }
    }

    void Wander()
    {
        if (m_Animator != null) m_Animator.SetBool("isAttacking", false);

        m_Timer += Time.deltaTime;

        if (m_Timer >= wanderTimer || (!m_Agent.pathPending && m_Agent.remainingDistance < 0.5f))
        {
            Vector3 newPos = RandomNavMeshLocation(wanderRadius);
            m_Agent.SetDestination(newPos);
            m_Timer = 0;
        }
    }

    void ChasePlayer()
    {
        if (m_Animator != null) m_Animator.SetBool("isAttacking", true);
        m_Agent.SetDestination(m_Player.position);
    }

    private Vector3 RandomNavMeshLocation(float radius)
    {
        Vector3 randomDirection = Random.insideUnitSphere * radius;
        randomDirection += transform.position;
        NavMeshHit hit;
        if (NavMesh.SamplePosition(randomDirection, out hit, radius, 1))
        {
            return hit.position;
        }
        return transform.position;
    }

    private void OnTriggerEnter(Collider other)
    {
        PlayerHealth health = other.GetComponent<PlayerHealth>();
        if (health != null)
        {
            health.SubPV(damage);
            Debug.Log("Touché !");
        }
    }
}
