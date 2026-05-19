using UnityEngine;
using UnityEngine.AI;

public class EnnemyControler : MonoBehaviour
{
    [Header("Stats")]
    [SerializeField] private int damage = 25;
    [SerializeField] private float detectionRange = 2f;
    [SerializeField] private LayerMask obstacleMask; // couches considérées comme murs
    [SerializeField] private float loseSightTime = 5f; // secondes avant d'abandonner la poursuite

    [Header("Mouvement Aléatoire")]
    [SerializeField] private float wanderRadius = 8f;
    [SerializeField] private float wanderTimer = 4f;

    private NavMeshAgent m_Agent;
    private Animator m_Animator;
    private Transform m_Player;
    [SerializeField] private float m_Timer;
    private bool isChasing = false;
    private float chaseLostTimer = 0f;
    private Vector3 lastKnownPosition;

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

        bool hasLOS = HasLineOfSight();

        if (distanceToPlayer <= detectionRange && hasLOS)
        {
            StartChase();
            lastKnownPosition = m_Player.position;
        }
        else if (isChasing)
        {
            // Si on était en poursuite mais on a perdu la ligne de vue
            HandleLostSight();
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

    void StartChase()
    {
        isChasing = true;
        chaseLostTimer = 0f;
        if (m_Animator != null) m_Animator.SetBool("isAttacking", true);
        m_Agent.SetDestination(m_Player.position);
    }

    void HandleLostSight()
    {
        // Aller vérifier la dernière position connue, puis abandonner après le délai
        chaseLostTimer += Time.deltaTime;
        m_Agent.SetDestination(lastKnownPosition);

        if ((!m_Agent.pathPending && m_Agent.remainingDistance <= m_Agent.stoppingDistance + 0.2f) || chaseLostTimer >= loseSightTime)
        {
            StopChaseAndResumePatrol();
        }
    }

    void StopChaseAndResumePatrol()
    {
        isChasing = false;
        chaseLostTimer = 0f;
        if (m_Animator != null) m_Animator.SetBool("isAttacking", false);
        // Forcer un nouveau point de patrouille
        m_Timer = wanderTimer;
        m_Agent.ResetPath();
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
        Vector3 eyePos = transform.position + Vector3.up * 1.0f;
        Vector3 direction = (m_Player.position - eyePos);
        float distance = direction.magnitude;
        RaycastHit[] hits = Physics.RaycastAll(eyePos, direction.normalized, distance);
        if (hits.Length == 0)
        {
            return true; // pas d'obstacle entre les deux
        }

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            if (h.collider == null) continue;
            if (h.collider.isTrigger) continue; // ignorer triggers
            if (h.collider.transform == m_Player || h.collider.CompareTag("Player"))
            {
                return true; // premier obstacle significatif est le joueur
            }
            // premier objet non-trigger rencontré n'est pas le joueur => vue bloquée
            return false;
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
