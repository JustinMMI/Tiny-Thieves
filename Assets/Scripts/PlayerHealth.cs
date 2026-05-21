using UnityEngine;
using UnityEngine.SceneManagement;

public class PlayerHealth : MonoBehaviour
{
    [SerializeField] private int MAX_PV = 100;
    [SerializeField] private int PV = 100;
    [SerializeField] private string DeadScene = "DeadScene";
    [SerializeField] private float healDelay = 15; // Time after taking damage before healing starts
    [SerializeField] private float healCooldown = 2; // Heal every 1 second
    [SerializeField] private int healAmount = 1; // Heal amount per tick
    [SerializeField] private int immunityTime = 2; // Time of immunity after taking damage
    private float healTimer = 0f;
    private float healDelayTimer = 0f;
    private float immunityTimer = 0f;

    void Start()
    {
        healTimer = 0f;
        healDelayTimer = 0f;
        immunityTimer = 0f;
        PV = MAX_PV; // Start with full health
    }

    public void SubPV(int damageAmount)
    {
        if (immunityTimer > 0)
        {
            PV -= damageAmount;
        }
        healDelay = 15; // Reset heal delay when taking damage
        immunityTimer = immunityTime; // Reset immunity timer
        if (PV <= 0)
        {
            Debug.Log("Player is dead!");
            SceneManager.LoadScene(DeadScene);
        }
        {
            return; // Skip damage if still immune
        }
    }

    public void AddPV(int healAmount)
    {
        PV = Mathf.Min(PV + healAmount, MAX_PV);
    }

    void Update()
    {
        // Only process healing if not at max health
        if (PV >= MAX_PV)
        {
            healDelay = 15f; // Reset heal delay when taking damage
            healDelayTimer = 0f;
            healTimer = 0f;
        }
        else
        {
            healDelayTimer += Time.deltaTime;
            if (healDelayTimer >= 1f)
            {
                healDelay--;
                healDelayTimer -= 1f;
            }
            if (healDelay <= 0)
            {
                healTimer += Time.deltaTime;
                if (healTimer >= healCooldown)
                {
                    PV = Mathf.Min(PV + healAmount, MAX_PV);
                    healTimer = 0f;
                }
            }
        }
    }
}
