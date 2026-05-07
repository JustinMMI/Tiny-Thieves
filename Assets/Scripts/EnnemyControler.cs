using UnityEngine;

public class EnnemyControler : MonoBehaviour
{
    [SerializeField] private int damage = 25;

    private void OnTriggerEnter(Collider other)
    {
        PlayerHealth playerHealth = other.GetComponent<PlayerHealth>();

        if (playerHealth != null)
        {
            playerHealth.SubPV(damage);
        }
    }
}
