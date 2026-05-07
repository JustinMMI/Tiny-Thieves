using UnityEngine;
using UnityEngine.SceneManagement;

public class PlayerHealth : MonoBehaviour
{
    [SerializeField] private int PV = 100;
    [SerializeField] private string DeadScene = "DeadScene";

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
    }

    public void SubPV(int damageAmount)
    {
        PV -= damageAmount;
        if (PV <= 0)
        {
            // Handle player death here
            Debug.Log("Player is dead!");
            SceneManager.LoadScene(DeadScene); // Reload the current scene
        }
    }

    public void AddPV(int healAmount)
    {
        PV += healAmount;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
