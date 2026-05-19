using UnityEngine;
using UnityEngine.SceneManagement;

public class PlayerHealth : MonoBehaviour
{
    [SerializeField] private int PV = 100;
    [SerializeField] private string DeadScene = "DeadScene";

    void Start()
    {
    }

    public void SubPV(int damageAmount)
    {
        PV -= damageAmount;
        if (PV <= 0)
        {
            Debug.Log("Player is dead!");
            SceneManager.LoadScene(DeadScene); // Load DeadScene
        }
    }

    public void AddPV(int healAmount)
    {
        PV += healAmount;
    }

    void Update()
    {
        
    }
}
