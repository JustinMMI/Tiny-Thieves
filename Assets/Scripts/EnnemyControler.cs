using UnityEngine;
using UnityEngine.AI;

public class EnnemyControler : MonoBehaviour
{
    [SerializeField] private int damage = 25;
    private NavMeshAgent m_Agent;
    private Animator m_Animator;
    [SerializeField] public float attackRange = 2f;
    private float m_Distance;
    public Transform Target;

    void Start()
    {
        m_Agent = GetComponent<NavMeshAgent>();
        m_Animator = GetComponent<Animator>();
    }

    void Update()
    {
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        m_Distance = Vector3.Distance(Target.position, player.transform.position);
        if (m_Distance <= attackRange)
        {
            m_Animator.SetBool("isWalking", false);
            m_Animator.SetBool("isAttacking", true);
            m_Animator.SetTrigger("Attack");
        }
        else
        {
            if (m_Agent.velocity.magnitude != 0f)
            {
                m_Animator.SetBool("isWalking", true);
            }
            else
            {
                m_Animator.SetBool("isWalking", false);
            }
            m_Animator.SetBool("isAttacking", false);
        }
    }

    void OnAnimatorMove()
    {
        if (m_Animator.GetBool("isAttacking") == false)
        {
            m_Agent.speed = (m_Animator.deltaPosition / Time.deltaTime).magnitude;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        PlayerHealth playerHealth = other.GetComponent<PlayerHealth>();

        if (playerHealth != null)
        {
            playerHealth.SubPV(damage);
        }
    }
}
