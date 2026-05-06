using UnityEngine;

[DisallowMultipleComponent]
public sealed class TinyRaymanBody : MonoBehaviour
{
    [Header("Visibility")]
    [SerializeField] private bool createOnAwake = true;
    [SerializeField] private bool hideLegacyModel = true;
    [SerializeField] private bool showTorsoInLocalView;
    [SerializeField] private bool showHeadInLocalView;

    [Header("Parts")]
    [SerializeField] private Vector3 torsoSize = new Vector3(0.2f, 0.2f, 0.14f);
    [SerializeField] private Vector3 headSize = new Vector3(0.14f, 0.14f, 0.14f);
    [SerializeField] private Vector3 handSize = new Vector3(0.07f, 0.06f, 0.07f);
    [SerializeField] private Vector3 footSize = new Vector3(0.09f, 0.05f, 0.13f);

    [Header("Motion")]
    [SerializeField] private float followSharpness = 18f;
    [SerializeField] private float limbBobAmount = 0.035f;
    [SerializeField] private float limbSwingAmount = 0.055f;
    [SerializeField] private float handGripSharpness = 22f;
    [SerializeField] private float handGripLift = 0.02f;

    [Header("Colors")]
    [SerializeField] private Color torsoColor = new Color(0.15f, 0.42f, 1f, 1f);
    [SerializeField] private Color headColor = new Color(1f, 0.82f, 0.42f, 1f);
    [SerializeField] private Color leftHandColor = new Color(1f, 0.25f, 0.2f, 1f);
    [SerializeField] private Color rightHandColor = new Color(0.2f, 1f, 0.35f, 1f);
    [SerializeField] private Color leftFootColor = new Color(1f, 0.2f, 0.9f, 1f);
    [SerializeField] private Color rightFootColor = new Color(1f, 0.85f, 0.15f, 1f);

    private const string RigName = "__Rayman_Local_Body";

    private Transform rig;
    private Transform torso;
    private Transform head;
    private Transform leftHand;
    private Transform rightHand;
    private Transform leftFoot;
    private Transform rightFoot;
    private CharacterController controller;
    private Vector3 previousPosition;
    private float moveCycle;
    private bool handsAttached;
    private Vector3 leftHandAnchor;
    private Vector3 rightHandAnchor;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        previousPosition = transform.position;

        if (createOnAwake)
        {
            EnsureBody();
        }

        if (hideLegacyModel)
        {
            HideLegacyPlayerModel();
        }
    }

    private void LateUpdate()
    {
        if (rig == null)
        {
            EnsureBody();
        }

        float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 velocity = (transform.position - previousPosition) / deltaTime;
        previousPosition = transform.position;

        Vector3 flatVelocity = Vector3.ProjectOnPlane(velocity, Vector3.up);
        float speed01 = Mathf.Clamp01(flatVelocity.magnitude / 2.2f);
        moveCycle += speed01 * deltaTime * 9f;

        float follow = 1f - Mathf.Exp(-followSharpness * deltaTime);
        UpdateCoreParts(speed01, follow);
        UpdateHands(speed01, follow, deltaTime);
        UpdateFeet(speed01, follow);
    }

    public void AttachHands(Vector3 leftAnchor, Vector3 rightAnchor)
    {
        handsAttached = true;
        leftHandAnchor = leftAnchor + Vector3.up * handGripLift;
        rightHandAnchor = rightAnchor + Vector3.up * handGripLift;
    }

    public void ReleaseHands()
    {
        handsAttached = false;
    }

    private void EnsureBody()
    {
        Transform existing = transform.Find(RigName);
        rig = existing != null ? existing : new GameObject(RigName).transform;
        rig.SetParent(transform, false);
        rig.localPosition = Vector3.zero;
        rig.localRotation = Quaternion.identity;
        rig.localScale = Vector3.one;

        torso = EnsurePart("Torso", torsoSize, torsoColor);
        head = EnsurePart("Head", headSize, headColor);
        leftHand = EnsurePart("Left Hand", handSize, leftHandColor);
        rightHand = EnsurePart("Right Hand", handSize, rightHandColor);
        leftFoot = EnsurePart("Left Foot", footSize, leftFootColor);
        rightFoot = EnsurePart("Right Foot", footSize, rightFootColor);
        ApplyLocalVisibility();
    }

    private Transform EnsurePart(string partName, Vector3 size, Color color)
    {
        Transform part = rig.Find(partName);
        if (part == null)
        {
            GameObject partObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            partObject.name = partName;
            part = partObject.transform;
            part.SetParent(rig, false);

            Collider collider = partObject.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        part.localScale = size;

        Renderer renderer = part.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = CreateMaterial(partName, color);
        }

        return part;
    }

    private void ApplyLocalVisibility()
    {
        SetPartVisible(torso, showTorsoInLocalView);
        SetPartVisible(head, showHeadInLocalView);
        SetPartVisible(leftHand, true);
        SetPartVisible(rightHand, true);
        SetPartVisible(leftFoot, true);
        SetPartVisible(rightFoot, true);
    }

    private static void SetPartVisible(Transform part, bool visible)
    {
        if (part == null)
        {
            return;
        }

        Renderer renderer = part.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.enabled = visible;
        }
    }

    private void UpdateCoreParts(float speed01, float follow)
    {
        float bodyBob = Mathf.Sin(moveCycle * 2f) * 0.01f * speed01;

        MoveLocal(torso, new Vector3(0f, 0.22f + bodyBob, 0.04f), follow);
        MoveLocal(head, new Vector3(0f, 0.36f + bodyBob * 0.5f, 0.08f), follow);
    }

    private void UpdateHands(float speed01, float follow, float deltaTime)
    {
        if (handsAttached)
        {
            float gripFollow = 1f - Mathf.Exp(-handGripSharpness * deltaTime);
            MoveWorld(leftHand, leftHandAnchor, gripFollow);
            MoveWorld(rightHand, rightHandAnchor, gripFollow);
            return;
        }

        float leftPhase = Mathf.Sin(moveCycle);
        float rightPhase = Mathf.Sin(moveCycle + Mathf.PI);
        MoveLocal(leftHand, new Vector3(-0.17f, 0.24f + leftPhase * limbBobAmount * speed01, 0.1f + leftPhase * limbSwingAmount * speed01), follow);
        MoveLocal(rightHand, new Vector3(0.17f, 0.24f + rightPhase * limbBobAmount * speed01, 0.1f + rightPhase * limbSwingAmount * speed01), follow);
    }

    private void UpdateFeet(float speed01, float follow)
    {
        float leftPhase = Mathf.Sin(moveCycle + Mathf.PI);
        float rightPhase = Mathf.Sin(moveCycle);
        float footY = controller != null ? Mathf.Max(0.035f, controller.radius * 0.2f) : 0.035f;

        MoveLocal(leftFoot, new Vector3(-0.08f, footY + Mathf.Max(0f, leftPhase) * limbBobAmount * speed01, 0.04f + leftPhase * limbSwingAmount * speed01), follow);
        MoveLocal(rightFoot, new Vector3(0.08f, footY + Mathf.Max(0f, rightPhase) * limbBobAmount * speed01, 0.04f + rightPhase * limbSwingAmount * speed01), follow);
    }

    private static void MoveLocal(Transform target, Vector3 localPosition, float follow)
    {
        if (target == null)
        {
            return;
        }

        target.localPosition = Vector3.Lerp(target.localPosition, localPosition, follow);
        target.localRotation = Quaternion.identity;
    }

    private static void MoveWorld(Transform target, Vector3 worldPosition, float follow)
    {
        if (target == null)
        {
            return;
        }

        target.position = Vector3.Lerp(target.position, worldPosition, follow);
    }

    private static Material CreateMaterial(string name, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            shader = Shader.Find("Hidden/Internal-Colored");
        }

        Material material = new Material(shader);
        material.name = "Rayman " + name;
        material.color = color;
        return material;
    }

    private void HideLegacyPlayerModel()
    {
        Transform legacyModel = transform.Find("Tiny Player Model");
        if (legacyModel == null)
        {
            return;
        }

        Renderer[] renderers = legacyModel.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].enabled = false;
        }

        Collider[] colliders = legacyModel.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }
    }
}
