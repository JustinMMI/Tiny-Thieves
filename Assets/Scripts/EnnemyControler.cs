using UnityEngine;
using UnityEngine.AI;

public class EnnemyControler : MonoBehaviour
{
    [Header("Stats")]
    [SerializeField] private int damage = 25;
    [SerializeField] private float detectionRange = 2f;
    [SerializeField] private LayerMask obstacleMask;
    [SerializeField] private float loseSightTime = 5f;

    [Header("Mouvement Aléatoire")]
    [SerializeField] private float wanderRadius = 8f;
    [SerializeField] private float wanderTimer = 4f;

    private NavMeshAgent m_Agent;
    private Animator m_Animator;
    private Transform m_Player;
    private float m_Timer;
    private float m_LostSightTimer;
    private bool m_IsChasing;

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

        if (distanceToPlayer <= detectionRange && HasLineOfSight())
        {
            m_IsChasing = true;
            m_LostSightTimer = 0f;
            if (m_Animator != null) m_Animator.SetBool("isAttacking", true);
            m_Agent.SetDestination(m_Player.position);
        }
        else if (m_IsChasing)
        {
            m_LostSightTimer += Time.deltaTime;
            m_Agent.SetDestination(m_Player.position);

            if (m_LostSightTimer >= loseSightTime)
            {
                m_IsChasing = false;
                m_LostSightTimer = 0f;
                if (m_Animator != null) m_Animator.SetBool("isAttacking", false);
                m_Timer = wanderTimer;
                m_Agent.ResetPath();
            }
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

    private bool HasLineOfSight()
    {
        if (m_Player == null) return false;

        Vector3 from = transform.position + Vector3.up * 1.0f;
        Vector3 to = m_Player.position + Vector3.up * 1.0f;
        Vector3 direction = (to - from).normalized;
        float distance = Vector3.Distance(from, to);

        int mask = obstacleMask.value == 0 ? Physics.DefaultRaycastLayers : obstacleMask.value;
        RaycastHit[] hits = Physics.RaycastAll(from, direction, distance, mask, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0)
        {
            return true;
        }

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null) continue;
            if (hit.collider.transform == transform) continue;

            return hit.collider.transform == m_Player || hit.collider.CompareTag("Player");
        }

        return true;
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
