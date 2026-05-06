using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Serialization;

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

    [Header("Character Model")]
    [SerializeField] private GameObject characterModel;
    [SerializeField] private Vector3 characterModelLocalPosition = Vector3.zero;
    [SerializeField] private Vector3 characterModelLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 characterModelLocalScale = Vector3.one;
    [SerializeField] private bool showCharacterModelInLocalView = true;
    [SerializeField] private bool applyCharacterModelOffsetToHitbox = true;

    [Header("Character Animation")]
    [SerializeField] private bool driveWalkAnimation = true;
    [SerializeField] private AnimationClip walkAnimationClip;
    [SerializeField] private RuntimeAnimatorController animatorController;
    [SerializeField] private string animatorWalkingBool = "IsWalking";
    [SerializeField] private string walkAnimationStateName = "walk";
    [SerializeField] private float walkAnimationSpeed = 1f;
    [SerializeField, Range(0f, 1f)] private float walkLoopStartNormalized = 0.22f;
    [SerializeField, Range(0f, 1f)] private float walkLoopEndNormalized = 0.78f;
    [SerializeField, Range(0f, 1f)] private float walkStopStartNormalized = 0.78f;
    [SerializeField] private float walkAnimationMoveThreshold = 0.05f;

    [Header("Hand Models")]
    [SerializeField] private GameObject leftHandModel;
    [SerializeField] private GameObject rightHandModel;
    [SerializeField] private Vector3 leftHandModelLocalPosition = Vector3.zero;
    [SerializeField] private Vector3 leftHandModelLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 leftHandModelLocalScale = Vector3.one;
    [SerializeField] private Vector3 rightHandModelLocalPosition = Vector3.zero;
    [SerializeField] private Vector3 rightHandModelLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 rightHandModelLocalScale = Vector3.one;

    [Header("Motion")]
    [SerializeField] private float followSharpness = 18f;
    [SerializeField] private float limbBobAmount = 0.035f;
    [SerializeField] private float limbSwingAmount = 0.055f;
    [SerializeField] private float handGripSharpness = 22f;
    [SerializeField] private float handGripLift = 0.02f;
    [FormerlySerializedAs("attachedHandWorldEulerAngles")]
    [SerializeField] private Vector3 attachedLeftHandWorldEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 attachedRightHandWorldEulerAngles = Vector3.zero;

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
    private Transform characterModelRoot;
    private Transform leftHand;
    private Transform rightHand;
    private Transform leftFoot;
    private Transform rightFoot;
    private Animator characterAnimator;
    private CharacterController controller;
    private Vector3 previousPosition;
    private float moveCycle;
    private bool handsAttached;
    private Vector3 leftHandAnchor;
    private Vector3 rightHandAnchor;
    private Quaternion attachedLeftHandRotation;
    private Quaternion attachedRightHandRotation;
    private bool attachedHandsSnap;
    private const string CustomModelName = "__Custom_Model";
    private const string CharacterModelName = "Character Model";
    private float walkAnimationTime = 1f;
    private bool wasWalking;
    private PlayableGraph walkGraph;
    private AnimationClipPlayable walkPlayable;

    public Vector3 HitboxLocalOffset => applyCharacterModelOffsetToHitbox && characterModel != null
        ? new Vector3(characterModelLocalPosition.x, 0f, characterModelLocalPosition.z)
        : Vector3.zero;

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

    private void OnValidate()
    {
        if (Application.isPlaying && rig != null)
        {
            EnsureBody();
        }
    }

    private void OnDisable()
    {
        DestroyWalkGraph();
    }

    private void OnDestroy()
    {
        DestroyWalkGraph();
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

        UpdateCharacterAnimation(speed01, deltaTime);

        float follow = 1f - Mathf.Exp(-followSharpness * deltaTime);
        UpdateCoreParts(speed01, follow);
        UpdateHands(speed01, follow, deltaTime);
        UpdateFeet(speed01, follow);
    }

    public void AttachHands(Vector3 leftAnchor, Vector3 rightAnchor)
    {
        AttachHands(
            leftAnchor,
            rightAnchor,
            Quaternion.Euler(attachedLeftHandWorldEulerAngles),
            Quaternion.Euler(attachedRightHandWorldEulerAngles));
    }

    public void AttachHands(Vector3 leftAnchor, Vector3 rightAnchor, Quaternion worldRotation)
    {
        AttachHands(leftAnchor, rightAnchor, worldRotation, worldRotation);
    }

    public void AttachHands(Vector3 leftAnchor, Vector3 rightAnchor, Quaternion leftWorldRotation, Quaternion rightWorldRotation)
    {
        AttachHands(leftAnchor, rightAnchor, leftWorldRotation, rightWorldRotation, false);
    }

    public void AttachHandsWithLocalOffsets(Vector3 leftAnchor, Vector3 rightAnchor, Quaternion leftBaseRotation, Quaternion rightBaseRotation)
    {
        AttachHands(
            leftAnchor,
            rightAnchor,
            leftBaseRotation * Quaternion.Euler(attachedLeftHandWorldEulerAngles),
            rightBaseRotation * Quaternion.Euler(attachedRightHandWorldEulerAngles));
    }

    public void AttachHands(
        Vector3 leftAnchor,
        Vector3 rightAnchor,
        Quaternion leftWorldRotation,
        Quaternion rightWorldRotation,
        bool snap)
    {
        handsAttached = true;
        leftHandAnchor = leftAnchor + Vector3.up * handGripLift;
        rightHandAnchor = rightAnchor + Vector3.up * handGripLift;
        attachedLeftHandRotation = leftWorldRotation;
        attachedRightHandRotation = rightWorldRotation;
        attachedHandsSnap = snap;
    }

    public void ReleaseHands()
    {
        handsAttached = false;
        attachedHandsSnap = false;
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
        characterModelRoot = EnsureCharacterModel();
        leftHand = EnsurePart("Left Hand", handSize, leftHandColor, leftHandModel, leftHandModelLocalPosition, leftHandModelLocalEulerAngles, leftHandModelLocalScale);
        rightHand = EnsurePart("Right Hand", handSize, rightHandColor, rightHandModel, rightHandModelLocalPosition, rightHandModelLocalEulerAngles, rightHandModelLocalScale);
        leftFoot = EnsurePart("Left Foot", footSize, leftFootColor);
        rightFoot = EnsurePart("Right Foot", footSize, rightFootColor);
        ApplyLocalVisibility();
    }

    private Transform EnsurePart(
        string partName,
        Vector3 size,
        Color color,
        GameObject modelPrefab = null,
        Vector3 modelLocalPosition = default(Vector3),
        Vector3 modelLocalEulerAngles = default(Vector3),
        Vector3 modelLocalScale = default(Vector3))
    {
        Transform part = rig.Find(partName);
        if (part == null)
        {
            GameObject partObject = modelPrefab == null
                ? GameObject.CreatePrimitive(PrimitiveType.Cube)
                : new GameObject(partName);
            partObject.name = partName;
            part = partObject.transform;
            part.SetParent(rig, false);

            Collider collider = partObject.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        part.localScale = modelPrefab == null ? size : Vector3.one;

        if (modelPrefab == null)
        {
            EnsureFallbackVisual(part, partName, color);
            RemoveCustomModel(part);
        }
        else
        {
            RemoveFallbackVisual(part);
            EnsureCustomModel(part, modelPrefab, modelLocalPosition, modelLocalEulerAngles, modelLocalScale == default(Vector3) ? Vector3.one : modelLocalScale);
        }

        return part;
    }

    private Transform EnsureCharacterModel()
    {
        Transform modelRoot = rig.Find(CharacterModelName);
        if (characterModel == null)
        {
            if (modelRoot != null)
            {
                DestroyGameObject(modelRoot.gameObject);
            }

            return null;
        }

        if (modelRoot == null)
        {
            modelRoot = new GameObject(CharacterModelName).transform;
            modelRoot.SetParent(rig, false);
        }

        modelRoot.localPosition = characterModelLocalPosition;
        modelRoot.localRotation = Quaternion.Euler(characterModelLocalEulerAngles);
        modelRoot.localScale = characterModelLocalScale;
        EnsureCustomModel(modelRoot, characterModel, Vector3.zero, Vector3.zero, Vector3.one);
        characterAnimator = modelRoot.GetComponentInChildren<Animator>(true);
        if (characterAnimator == null)
        {
            characterAnimator = modelRoot.gameObject.AddComponent<Animator>();
        }

        if (characterAnimator != null)
        {
            characterAnimator.applyRootMotion = false;
            characterAnimator.speed = 1f;
            if (animatorController != null)
            {
                characterAnimator.runtimeAnimatorController = animatorController;
            }
        }

        SetupWalkPlayable();
        return modelRoot;
    }

    private void EnsureFallbackVisual(Transform part, string partName, Color color)
    {
        if (part.GetComponent<MeshFilter>() == null)
        {
            MeshFilter meshFilter = part.gameObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateCubeMesh();
        }

        Renderer renderer = part.GetComponent<Renderer>();
        if (renderer == null)
        {
            renderer = part.gameObject.AddComponent<MeshRenderer>();
        }

        renderer.sharedMaterial = CreateMaterial(partName, color);
    }

    private void RemoveFallbackVisual(Transform part)
    {
        MeshRenderer meshRenderer = part.GetComponent<MeshRenderer>();
        MeshFilter meshFilter = part.GetComponent<MeshFilter>();

        DestroyComponent(meshRenderer);
        DestroyComponent(meshFilter);

        Collider collider = part.GetComponent<Collider>();
        DestroyComponent(collider);
    }

    private void EnsureCustomModel(Transform part, GameObject modelPrefab, Vector3 localPosition, Vector3 localEulerAngles, Vector3 localScale)
    {
        Transform customModel = part.Find(CustomModelName);
        if (customModel == null)
        {
            GameObject instance = Instantiate(modelPrefab, part);
            instance.name = CustomModelName;
            customModel = instance.transform;
        }

        customModel.localPosition = localPosition;
        customModel.localRotation = Quaternion.Euler(localEulerAngles);
        customModel.localScale = localScale;

        Collider[] colliders = customModel.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }
    }

    private void RemoveCustomModel(Transform part)
    {
        Transform customModel = part.Find(CustomModelName);
        if (customModel != null)
        {
            DestroyGameObject(customModel.gameObject);
        }
    }

    private void ApplyLocalVisibility()
    {
        bool usesCharacterModel = characterModelRoot != null;
        SetPartVisible(characterModelRoot, usesCharacterModel && showCharacterModelInLocalView);
        SetPartVisible(torso, !usesCharacterModel && showTorsoInLocalView);
        SetPartVisible(head, !usesCharacterModel && showHeadInLocalView);
        SetPartVisible(leftHand, true);
        SetPartVisible(rightHand, true);
        SetPartVisible(leftFoot, !usesCharacterModel);
        SetPartVisible(rightFoot, !usesCharacterModel);
    }

    private static void SetPartVisible(Transform part, bool visible)
    {
        if (part == null)
        {
            return;
        }

        Renderer[] renderers = part.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].enabled = visible;
        }
    }

    private void UpdateCoreParts(float speed01, float follow)
    {
        if (characterModelRoot != null)
        {
            return;
        }

        float bodyBob = Mathf.Sin(moveCycle * 2f) * 0.01f * speed01;

        MoveLocal(torso, new Vector3(0f, 0.22f + bodyBob, 0.04f), follow);
        MoveLocal(head, new Vector3(0f, 0.36f + bodyBob * 0.5f, 0.08f), follow);
    }

    private void UpdateCharacterAnimation(float speed01, float deltaTime)
    {
        if (!driveWalkAnimation || characterAnimator == null)
        {
            return;
        }

        bool isWalking = speed01 > walkAnimationMoveThreshold;
        float loopStart = Mathf.Clamp01(walkLoopStartNormalized);
        float loopEnd = Mathf.Clamp(walkLoopEndNormalized, loopStart + 0.01f, 1f);
        float stopStart = Mathf.Clamp(walkStopStartNormalized, loopStart, 1f);

        if (isWalking)
        {
            if (!wasWalking)
            {
                walkAnimationTime = 0f;
            }

            walkAnimationTime += deltaTime * Mathf.Max(0.01f, walkAnimationSpeed);
            if (walkAnimationTime >= loopEnd)
            {
                float loopLength = Mathf.Max(0.01f, loopEnd - loopStart);
                walkAnimationTime = loopStart + Mathf.Repeat(walkAnimationTime - loopStart, loopLength);
            }
        }
        else if (wasWalking)
        {
            walkAnimationTime = stopStart;
        }
        else if (walkAnimationTime < 1f)
        {
            walkAnimationTime = Mathf.Min(1f, walkAnimationTime + deltaTime * Mathf.Max(0.01f, walkAnimationSpeed));
        }

        wasWalking = isWalking;
        if (animatorController != null && !string.IsNullOrEmpty(walkAnimationStateName))
        {
            characterAnimator.SetBool(animatorWalkingBool, isWalking);
            characterAnimator.Play(walkAnimationStateName, 0, walkAnimationTime);
            characterAnimator.Update(0f);
            return;
        }

        if (!walkGraph.IsValid() || !walkPlayable.IsValid() || walkAnimationClip == null)
        {
            return;
        }

        walkPlayable.SetTime(Mathf.Clamp01(walkAnimationTime) * walkAnimationClip.length);
        walkGraph.Evaluate(0f);
    }

    private void SetupWalkPlayable()
    {
        DestroyWalkGraph();
        if (walkAnimationClip == null || characterAnimator == null || animatorController != null)
        {
            return;
        }

        walkGraph = PlayableGraph.Create("Tiny Character Walk");
        walkGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        walkPlayable = AnimationClipPlayable.Create(walkGraph, walkAnimationClip);
        walkPlayable.SetApplyFootIK(false);
        walkPlayable.SetSpeed(0f);

        AnimationPlayableOutput output = AnimationPlayableOutput.Create(walkGraph, "Walk", characterAnimator);
        output.SetSourcePlayable(walkPlayable);
        walkGraph.Play();
    }

    private void DestroyWalkGraph()
    {
        if (walkGraph.IsValid())
        {
            walkGraph.Destroy();
        }
    }

    private void UpdateHands(float speed01, float follow, float deltaTime)
    {
        if (handsAttached)
        {
            if (attachedHandsSnap)
            {
                SetWorld(leftHand, leftHandAnchor, attachedLeftHandRotation);
                SetWorld(rightHand, rightHandAnchor, attachedRightHandRotation);
                return;
            }

            float gripFollow = 1f - Mathf.Exp(-handGripSharpness * deltaTime);
            MoveWorld(leftHand, leftHandAnchor, gripFollow);
            MoveWorld(rightHand, rightHandAnchor, gripFollow);
            RotateWorld(leftHand, attachedLeftHandRotation, gripFollow);
            RotateWorld(rightHand, attachedRightHandRotation, gripFollow);
            return;
        }

        float leftPhase = Mathf.Sin(moveCycle);
        float rightPhase = Mathf.Sin(moveCycle + Mathf.PI);
        MoveLocal(leftHand, new Vector3(-0.17f, 0.24f + leftPhase * limbBobAmount * speed01, 0.1f + leftPhase * limbSwingAmount * speed01), follow);
        MoveLocal(rightHand, new Vector3(0.17f, 0.24f + rightPhase * limbBobAmount * speed01, 0.1f + rightPhase * limbSwingAmount * speed01), follow);
    }

    private void UpdateFeet(float speed01, float follow)
    {
        if (characterModelRoot != null)
        {
            return;
        }

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

    private static void RotateWorld(Transform target, Quaternion worldRotation, float follow)
    {
        if (target == null)
        {
            return;
        }

        target.rotation = Quaternion.Slerp(target.rotation, worldRotation, follow);
    }

    private static void SetWorld(Transform target, Vector3 worldPosition, Quaternion worldRotation)
    {
        if (target == null)
        {
            return;
        }

        target.SetPositionAndRotation(worldPosition, worldRotation);
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

    private static Mesh CreateCubeMesh()
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh mesh = cube.GetComponent<MeshFilter>().sharedMesh;
        DestroyGameObject(cube);
        return mesh;
    }

    private static void DestroyComponent(Component component)
    {
        if (component == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(component);
        }
        else
        {
            DestroyImmediate(component);
        }
    }

    private static void DestroyGameObject(GameObject target)
    {
        if (target == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
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
