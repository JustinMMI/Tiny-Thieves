using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
public sealed class TinyRaymanBody : MonoBehaviour
{
    private enum MoveAnimationKind
    {
        Walk,
        Strafe
    }

    private enum PlayerSkin
    {
        Vert,
        Rouge,
        Bleu,
        Orange
    }

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

    [Header("Character Skin")]
    [SerializeField] private PlayerSkin selectedSkin;
    [SerializeField] private string bodyRendererName = "Body";
    [SerializeField] private Material greenBodyMaterial;
    [SerializeField] private Material redBodyMaterial;
    [SerializeField] private Material blueBodyMaterial;
    [SerializeField] private Material orangeBodyMaterial;
    [SerializeField] private string greenMaskName = "Mask Green";
    [SerializeField] private string redMaskName = "Mask Red";
    [SerializeField] private string blueMaskName = "Mask Blue";
    [SerializeField] private string orangeMaskName = "Mask Orange";
    [SerializeField] private string whiteMaskName = "Mask White";

    [Header("Head Look")]
    [SerializeField] private bool driveHeadLookWithCamera = true;
    [SerializeField] private string headLookTransformName = "BonesT\u00EAte";
    [SerializeField] private float headLookPitchInfluence = 0.65f;
    [SerializeField] private float headLookMinPitch = -35f;
    [SerializeField] private float headLookMaxPitch = 45f;
    [SerializeField] private float headLookSharpness = 18f;
    [SerializeField] private Vector3 headLookLocalEulerAxis = Vector3.right;

    [Header("Character Animation")]
    [SerializeField] private bool driveWalkAnimation = true;
    [SerializeField] private AnimationClip idleAnimationClip;
    [SerializeField] private AnimationClip walkAnimationClip;
    [SerializeField] private AnimationClip strafeWalkAnimationClip;
    [SerializeField] private AnimationClip jumpAnimationClip;
    [SerializeField] private RuntimeAnimatorController animatorController;
    [SerializeField] private string animatorWalkingBool = "IsWalking";
    [SerializeField] private string idleAnimationStateName = "Idle";
    [SerializeField] private string walkAnimationStateName = "walk";
    [SerializeField] private string strafeWalkAnimationStateName = "StraffWalk";
    [SerializeField] private string jumpAnimationStateName = "Jump";
    [SerializeField] private float idleAnimationSpeed = 1f;
    [SerializeField] private float walkAnimationSpeed = 1f;
    [SerializeField] private float walkStopAnimationSpeed = 1f;
    [SerializeField] private float jumpAnimationSpeed = 1f;
    [SerializeField] private float airborneVelocityThreshold = 0.08f;
    [SerializeField, Range(0.01f, 1f)] private float jumpAnimationEndNormalized = 0.65f;
    [SerializeField] private float walkAnimationReferenceSpeed = 1.65f;
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
    [SerializeField] private float jumpHandLift = 0.055f;
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
    private bool leftHandAttached;
    private bool rightHandAttached;
    private Vector3 leftHandAnchor;
    private Vector3 rightHandAnchor;
    private Quaternion attachedLeftHandRotation;
    private Quaternion attachedRightHandRotation;
    private bool attachedHandsSnap;
    private const string CustomModelName = "__Custom_Model";
    private const string CharacterModelName = "Character Model";
    private float idleAnimationTime;
    private float walkAnimationTime = 1f;
    private bool wasWalking;
    private bool wasWalkingReversed;
    private bool isAirborneAnimationActive;
    private bool isJumpAnimationActive;
    private bool airborneHasLeftGround;
    private bool remoteAirborneOverride;
    private bool remoteIsAirborne;
    private float airAnimationTime;
    private MoveAnimationKind currentMoveAnimationKind = MoveAnimationKind.Walk;
    private MoveAnimationKind previousMoveAnimationKind = MoveAnimationKind.Walk;
    private AnimationClip activePlayableClip;
    private PlayableGraph walkGraph;
    private AnimationClipPlayable walkPlayable;
    private Transform headLookTransform;
    private Quaternion headLookBaseLocalRotation = Quaternion.identity;
    private float targetHeadLookPitch;
    private float currentHeadLookPitch;
    private bool headLookHasPreferredTarget;
    private bool headLookNeedsAnimatorRefresh;
    private int jumpSequence;

    public Vector3 HitboxLocalOffset => characterModel != null
        ? new Vector3(characterModelLocalPosition.x, 0f, characterModelLocalPosition.z)
        : Vector3.zero;
    public int CurrentSkinIndex => (int)selectedSkin;

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
        float flatSpeed = flatVelocity.magnitude;
        float speed01 = Mathf.Clamp01(flatSpeed / 2.2f);
        float verticalSpeed = velocity.y;
        bool isGrounded = controller != null && controller.enabled
            ? controller.isGrounded
            : Mathf.Abs(verticalSpeed) <= airborneVelocityThreshold;

        moveCycle += speed01 * deltaTime * 9f;
        float forwardAmount = flatVelocity.sqrMagnitude > 0.0001f
            ? Vector3.Dot(flatVelocity.normalized, transform.forward)
            : 0f;
        float strafeAmount = flatVelocity.sqrMagnitude > 0.0001f
            ? Vector3.Dot(flatVelocity.normalized, transform.right)
            : 0f;

        UpdateCharacterAnimation(speed01, flatSpeed, forwardAmount, strafeAmount, verticalSpeed, isGrounded, deltaTime);

        float follow = 1f - Mathf.Exp(-followSharpness * deltaTime);
        UpdateCoreParts(speed01, follow);
        UpdateHands(speed01, follow, deltaTime);
        UpdateFeet(speed01, follow);
        UpdateHeadLook(deltaTime);
    }

    public void SetCameraPitch(float cameraPitch)
    {
        targetHeadLookPitch = Mathf.Clamp(cameraPitch * headLookPitchInfluence, headLookMinPitch, headLookMaxPitch);
    }

    public void SetCameraLook(float cameraPitch, Quaternion cameraWorldRotation)
    {
        SetCameraPitch(cameraPitch);
    }

    public void NotifyJump()
    {
        jumpSequence++;
        BeginJumpAnimation();
    }

    public void ApplyRemoteJumpState(int sequence, bool isAirborne)
    {
        if (sequence > jumpSequence)
        {
            jumpSequence = sequence;
            BeginJumpAnimation();
        }

        if (isAirborne && isAirborneAnimationActive)
        {
            remoteAirborneOverride = true;
            remoteIsAirborne = true;
            return;
        }

        if (!isAirborne && remoteAirborneOverride)
        {
            remoteIsAirborne = false;
            remoteAirborneOverride = false;
            isAirborneAnimationActive = false;
            isJumpAnimationActive = false;
            airborneHasLeftGround = false;
            airAnimationTime = 0f;
        }
    }

    public bool IsJumpAirborne => isAirborneAnimationActive;
    public float HandGripLift => handGripLift;

    private void BeginJumpAnimation()
    {
        isAirborneAnimationActive = true;
        isJumpAnimationActive = true;
        airborneHasLeftGround = false;
        airAnimationTime = 0f;
    }

    public int JumpSequence => jumpSequence;

    public void TriggerLegoBreakAndDestroy(float destroyDelay, bool destroyOwnerAfterDelay = false)
    {
        enabled = false;
        if (characterAnimator != null)
        {
            characterAnimator.enabled = false;
        }

        Transform[] pieces =
        {
            characterModelRoot,
            torso,
            head,
            leftHand,
            rightHand,
            leftFoot,
            rightFoot
        };

        for (int i = 0; i < pieces.Length; i++)
        {
            DetachPhysicsPiece(pieces[i], i);
            if (pieces[i] != null)
            {
                Destroy(pieces[i].gameObject, Mathf.Max(0f, destroyDelay));
            }
        }

        if (destroyOwnerAfterDelay)
        {
            StartCoroutine(DestroyOwnerAfterDelay(Mathf.Max(0f, destroyDelay)));
        }
    }

    public void SetSkin(int skinIndex)
    {
        selectedSkin = (PlayerSkin)Mathf.Clamp(skinIndex, 0, Enum.GetValues(typeof(PlayerSkin)).Length - 1);
        ApplyCharacterSkin();
    }

    public bool TryGetHandPoses(
        out Vector3 leftPosition,
        out Quaternion leftRotation,
        out Vector3 rightPosition,
        out Quaternion rightRotation)
    {
        if (leftHand == null || rightHand == null)
        {
            leftPosition = Vector3.zero;
            leftRotation = Quaternion.identity;
            rightPosition = Vector3.zero;
            rightRotation = Quaternion.identity;
            return false;
        }

        leftPosition = leftHand.position;
        leftRotation = leftHand.rotation;
        rightPosition = rightHand.position;
        rightRotation = rightHand.rotation;
        return true;
    }

    public void ApplyRemoteHandPoses(
        Vector3 leftPosition,
        Quaternion leftRotation,
        Vector3 rightPosition,
        Quaternion rightRotation,
        bool snap)
    {
        AttachHands(leftPosition - Vector3.up * handGripLift, rightPosition - Vector3.up * handGripLift, leftRotation, rightRotation, snap);
    }

    public void ApplyRemoteHandAnchors(
        Vector3 leftAnchor,
        Quaternion leftRotation,
        Vector3 rightAnchor,
        Quaternion rightRotation,
        bool snap)
    {
        AttachHands(leftAnchor, rightAnchor, leftRotation, rightRotation, snap);
    }

    public void ApplyRemoteHandAnchors(
        Vector3 leftAnchor,
        Quaternion leftRotation,
        Vector3 rightAnchor,
        Quaternion rightRotation,
        bool leftActive,
        bool rightActive,
        bool snap)
    {
        AttachHands(leftAnchor, rightAnchor, leftRotation, rightRotation, leftActive, rightActive, snap);
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
        AttachHandsWithLocalOffsets(leftAnchor, rightAnchor, leftBaseRotation, rightBaseRotation, true, true);
    }

    public void AttachHandsWithLocalOffsets(
        Vector3 leftAnchor,
        Vector3 rightAnchor,
        Quaternion leftBaseRotation,
        Quaternion rightBaseRotation,
        bool leftActive,
        bool rightActive,
        bool snap = false)
    {
        AttachHands(
            leftAnchor,
            rightAnchor,
            GetLeftAttachedHandRotation(leftBaseRotation),
            GetRightAttachedHandRotation(rightBaseRotation),
            leftActive,
            rightActive,
            snap);
    }

    public Quaternion GetLeftAttachedHandRotation(Quaternion baseRotation)
    {
        return baseRotation * Quaternion.Euler(attachedLeftHandWorldEulerAngles);
    }

    public Quaternion GetRightAttachedHandRotation(Quaternion baseRotation)
    {
        return baseRotation * Quaternion.Euler(attachedRightHandWorldEulerAngles);
    }

    public void AttachHands(
        Vector3 leftAnchor,
        Vector3 rightAnchor,
        Quaternion leftWorldRotation,
        Quaternion rightWorldRotation,
        bool snap)
    {
        AttachHands(leftAnchor, rightAnchor, leftWorldRotation, rightWorldRotation, true, true, snap);
    }

    public void AttachHands(
        Vector3 leftAnchor,
        Vector3 rightAnchor,
        Quaternion leftWorldRotation,
        Quaternion rightWorldRotation,
        bool leftActive,
        bool rightActive,
        bool snap = false)
    {
        handsAttached = leftActive || rightActive;
        leftHandAttached = leftActive;
        rightHandAttached = rightActive;
        leftHandAnchor = leftAnchor + Vector3.up * handGripLift;
        rightHandAnchor = rightAnchor + Vector3.up * handGripLift;
        attachedLeftHandRotation = leftWorldRotation;
        attachedRightHandRotation = rightWorldRotation;
        attachedHandsSnap = snap;
    }

    public void ReleaseHands()
    {
        handsAttached = false;
        leftHandAttached = false;
        rightHandAttached = false;
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
        ApplyCharacterSkin();
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
        ConfigureCharacterRenderers(modelRoot);
        ApplyCharacterSkin();
        characterAnimator = modelRoot.GetComponentInChildren<Animator>(true);
        if (characterAnimator == null)
        {
            characterAnimator = modelRoot.gameObject.AddComponent<Animator>();
        }

        if (characterAnimator != null)
        {
            characterAnimator.applyRootMotion = false;
            characterAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            characterAnimator.speed = 1f;
            if (animatorController != null)
            {
                characterAnimator.runtimeAnimatorController = animatorController;
            }
        }

        SetupWalkPlayable();
        characterAnimator.Update(0f);
        ResolveHeadLookTransform();
        headLookNeedsAnimatorRefresh = true;
        return modelRoot;
    }

    private static void ConfigureCharacterRenderers(Transform modelRoot)
    {
        if (modelRoot == null)
        {
            return;
        }

        SkinnedMeshRenderer[] skinnedRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            skinnedRenderers[i].updateWhenOffscreen = true;
        }
    }

    private void ApplyCharacterSkin()
    {
        Transform modelInstance = characterModelRoot != null ? characterModelRoot.Find(CustomModelName) : null;
        Transform skinRoot = modelInstance != null ? modelInstance : characterModelRoot;
        if (skinRoot == null)
        {
            return;
        }

        Material bodyMaterial = GetSelectedBodyMaterial();
        if (bodyMaterial != null)
        {
            Renderer[] bodyRenderers = FindRenderersByName(skinRoot, bodyRendererName);
            for (int i = 0; i < bodyRenderers.Length; i++)
            {
                ApplyMaterialToRenderer(bodyRenderers[i], bodyMaterial);
            }
        }

        SetMaskVisible(skinRoot, whiteMaskName, false);
        SetMaskVisible(skinRoot, greenMaskName, selectedSkin == PlayerSkin.Vert);
        SetMaskVisible(skinRoot, redMaskName, selectedSkin == PlayerSkin.Rouge);
        SetMaskVisible(skinRoot, blueMaskName, selectedSkin == PlayerSkin.Bleu);
        SetMaskVisible(skinRoot, orangeMaskName, selectedSkin == PlayerSkin.Orange);
    }

    private Material GetSelectedBodyMaterial()
    {
        switch (selectedSkin)
        {
            case PlayerSkin.Rouge:
                return redBodyMaterial;
            case PlayerSkin.Bleu:
                return blueBodyMaterial;
            case PlayerSkin.Orange:
                return orangeBodyMaterial;
            default:
                return greenBodyMaterial;
        }
    }

    private static void SetMaskVisible(Transform root, string maskName, bool visible)
    {
        Transform mask = FindTransformByName(root, maskName);
        if (mask != null)
        {
            mask.gameObject.SetActive(visible);
        }
    }

    private static Renderer[] FindRenderersByName(Transform root, string rendererName)
    {
        Transform rendererTransform = FindTransformByName(root, rendererName);
        if (rendererTransform == null)
        {
            return Array.Empty<Renderer>();
        }

        return rendererTransform.GetComponentsInChildren<Renderer>(true);
    }

    private static void ApplyMaterialToRenderer(Renderer renderer, Material material)
    {
        if (renderer == null || material == null)
        {
            return;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials.Length == 0)
        {
            renderer.sharedMaterial = material;
            return;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = material;
        }

        renderer.sharedMaterials = materials;
    }

    private void ResolveHeadLookTransform()
    {
        headLookTransform = null;
        headLookHasPreferredTarget = false;
        if (characterModelRoot != null)
        {
            headLookTransform = FindPreferredHeadLookTransform(characterModelRoot);
            headLookHasPreferredTarget = headLookTransform != null;
            if (headLookTransform == null)
            {
                headLookTransform = FindTransformByName(characterModelRoot, headLookTransformName);
            }
            if (headLookTransform == null)
            {
                headLookTransform = FindTransformByName(characterModelRoot, "head");
            }
            if (headLookTransform == null)
            {
                headLookTransform = FindTransformByName(characterModelRoot, "tete");
            }
            if (headLookTransform == null)
            {
                headLookTransform = FindTransformByName(characterModelRoot, "neck");
            }
        }
        else
        {
            headLookTransform = head;
            headLookHasPreferredTarget = true;
        }

        headLookBaseLocalRotation = headLookTransform != null
            ? headLookTransform.localRotation
            : Quaternion.identity;
        currentHeadLookPitch = targetHeadLookPitch;
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

    private void UpdateCharacterAnimation(float speed01, float flatSpeed, float forwardAmount, float strafeAmount, float verticalSpeed, bool isGrounded, float deltaTime)
    {
        if (!driveWalkAnimation || characterAnimator == null)
        {
            return;
        }

        if (TryPlayAirAnimation(verticalSpeed, isGrounded, deltaTime))
        {
            return;
        }

        bool isWalking = speed01 > walkAnimationMoveThreshold;
        bool useStrafeAnimation = isWalking && Mathf.Abs(strafeAmount) > Mathf.Abs(forwardAmount) && Mathf.Abs(strafeAmount) > 0.2f;
        currentMoveAnimationKind = useStrafeAnimation ? MoveAnimationKind.Strafe : MoveAnimationKind.Walk;
        bool isAnimationReversed = isWalking && (useStrafeAnimation ? strafeAmount < -0.2f : forwardAmount < -0.2f);
        float loopStart = Mathf.Clamp01(walkLoopStartNormalized);
        float loopEnd = Mathf.Clamp(walkLoopEndNormalized, loopStart + 0.01f, 1f);
        float stopStart = Mathf.Clamp(walkStopStartNormalized, loopStart, 1f);
        float speedMultiplier = Mathf.Max(0.05f, flatSpeed / Mathf.Max(0.01f, walkAnimationReferenceSpeed));
        float loopBaseTimeStep = deltaTime * Mathf.Max(0.01f, walkAnimationSpeed);
        float loopTimeStep = loopBaseTimeStep * speedMultiplier;
        float stopTimeStep = deltaTime * Mathf.Max(0.01f, walkStopAnimationSpeed);

        if (isWalking)
        {
            if (!wasWalking || wasWalkingReversed != isAnimationReversed || previousMoveAnimationKind != currentMoveAnimationKind)
            {
                walkAnimationTime = isAnimationReversed ? loopEnd : 0f;
            }

            if (isAnimationReversed)
            {
                walkAnimationTime -= loopTimeStep;
                if (walkAnimationTime <= loopStart)
                {
                    float loopLength = Mathf.Max(0.01f, loopEnd - loopStart);
                    walkAnimationTime = loopEnd - Mathf.Repeat(loopStart - walkAnimationTime, loopLength);
                }
            }
            else
            {
                walkAnimationTime += loopTimeStep;
                if (walkAnimationTime >= loopEnd)
                {
                    float loopLength = Mathf.Max(0.01f, loopEnd - loopStart);
                    walkAnimationTime = loopStart + Mathf.Repeat(walkAnimationTime - loopStart, loopLength);
                }
            }
        }
        else if (wasWalking)
        {
            walkAnimationTime = stopStart;
        }
        else if (walkAnimationTime < 1f)
        {
            walkAnimationTime = Mathf.Min(1f, walkAnimationTime + stopTimeStep);
        }

        wasWalking = isWalking;
        wasWalkingReversed = isAnimationReversed;
        previousMoveAnimationKind = currentMoveAnimationKind;

        if (!isWalking && walkAnimationTime >= 1f && TryPlayIdleAnimation(deltaTime))
        {
            return;
        }

        string stateName = GetCurrentAnimationStateName();
        if (animatorController != null && !string.IsNullOrEmpty(stateName))
        {
            characterAnimator.SetBool(animatorWalkingBool, isWalking);
            characterAnimator.Play(stateName, 0, walkAnimationTime);
            characterAnimator.Update(0f);
            return;
        }

        AnimationClip clip = GetCurrentAnimationClip();
        if (clip == null)
        {
            return;
        }

        EnsureWalkPlayable(clip);
        if (!walkGraph.IsValid() || !walkPlayable.IsValid())
        {
            return;
        }

        walkPlayable.SetTime(Mathf.Clamp01(walkAnimationTime) * clip.length);
        walkGraph.Evaluate(0f);
    }

    private bool TryPlayAirAnimation(float verticalSpeed, bool isGrounded, float deltaTime)
    {
        bool effectiveGrounded = remoteAirborneOverride ? !remoteIsAirborne : isGrounded;
        bool shouldBeAirborne = isAirborneAnimationActive;
        if (!effectiveGrounded)
        {
            airborneHasLeftGround = true;
        }

        if (!shouldBeAirborne && !isAirborneAnimationActive)
        {
            return false;
        }

        if (effectiveGrounded && airborneHasLeftGround && isAirborneAnimationActive)
        {
            isAirborneAnimationActive = false;
            isJumpAnimationActive = false;
            airborneHasLeftGround = false;
            remoteAirborneOverride = false;
            remoteIsAirborne = false;
            airAnimationTime = 0f;
            return false;
        }

        if (!isAirborneAnimationActive)
        {
            return false;
        }

        float jumpEnd = Mathf.Clamp01(jumpAnimationEndNormalized);
        if (!HasJumpAnimation())
        {
            isAirborneAnimationActive = false;
            isJumpAnimationActive = false;
            airAnimationTime = 0f;
            return false;
        }

        string stateName = jumpAnimationStateName;
        AnimationClip clip = jumpAnimationClip;
        float speed = jumpAnimationSpeed;

        if (string.IsNullOrEmpty(stateName) && clip == null)
        {
            return false;
        }

        airAnimationTime = Mathf.Min(jumpEnd, airAnimationTime + deltaTime * Mathf.Max(0.01f, speed));
        isJumpAnimationActive = airAnimationTime < jumpEnd;

        wasWalking = false;
        walkAnimationTime = 1f;

        if (animatorController != null && !string.IsNullOrEmpty(stateName))
        {
            characterAnimator.SetBool(animatorWalkingBool, false);
            characterAnimator.Play(stateName, 0, airAnimationTime);
            characterAnimator.Update(0f);
            return true;
        }

        if (clip == null)
        {
            return false;
        }

        EnsureWalkPlayable(clip);
        if (!walkGraph.IsValid() || !walkPlayable.IsValid())
        {
            return false;
        }

        walkPlayable.SetTime(Mathf.Clamp01(airAnimationTime) * clip.length);
        walkGraph.Evaluate(0f);
        return true;
    }

    private bool HasJumpAnimation()
    {
        return animatorController != null
            ? !string.IsNullOrEmpty(jumpAnimationStateName)
            : jumpAnimationClip != null;
    }

    private bool TryPlayIdleAnimation(float deltaTime)
    {
        if (idleAnimationClip == null && string.IsNullOrEmpty(idleAnimationStateName))
        {
            return false;
        }

        idleAnimationTime = Mathf.Repeat(
            idleAnimationTime + deltaTime * Mathf.Max(0.01f, idleAnimationSpeed),
            1f);

        if (animatorController != null && !string.IsNullOrEmpty(idleAnimationStateName))
        {
            characterAnimator.SetBool(animatorWalkingBool, false);
            characterAnimator.Play(idleAnimationStateName, 0, idleAnimationTime);
            characterAnimator.Update(0f);
            return true;
        }

        if (idleAnimationClip == null)
        {
            return false;
        }

        EnsureWalkPlayable(idleAnimationClip);
        if (!walkGraph.IsValid() || !walkPlayable.IsValid())
        {
            return false;
        }

        walkPlayable.SetTime(idleAnimationTime * idleAnimationClip.length);
        walkGraph.Evaluate(0f);
        return true;
    }

    private AnimationClip GetCurrentAnimationClip()
    {
        if (currentMoveAnimationKind == MoveAnimationKind.Strafe && strafeWalkAnimationClip != null)
        {
            return strafeWalkAnimationClip;
        }

        return walkAnimationClip;
    }

    private string GetCurrentAnimationStateName()
    {
        if (currentMoveAnimationKind == MoveAnimationKind.Strafe && !string.IsNullOrEmpty(strafeWalkAnimationStateName))
        {
            return strafeWalkAnimationStateName;
        }

        return walkAnimationStateName;
    }

    private void SetupWalkPlayable()
    {
        DestroyWalkGraph();
        if (characterAnimator == null || animatorController != null)
        {
            return;
        }

        EnsureWalkPlayable(idleAnimationClip != null ? idleAnimationClip : walkAnimationClip);
    }

    private void EnsureWalkPlayable(AnimationClip clip)
    {
        if (clip == null || characterAnimator == null || animatorController != null)
        {
            return;
        }

        if (walkGraph.IsValid() && walkPlayable.IsValid() && activePlayableClip == clip)
        {
            return;
        }

        DestroyWalkGraph();
        activePlayableClip = clip;
        walkGraph = PlayableGraph.Create("Tiny Character Walk");
        walkGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        walkPlayable = AnimationClipPlayable.Create(walkGraph, clip);
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

        activePlayableClip = null;
    }

    private void UpdateHands(float speed01, float follow, float deltaTime)
    {
        float leftPhase = Mathf.Sin(moveCycle);
        float rightPhase = Mathf.Sin(moveCycle + Mathf.PI);
        float airborneHandOffset = GetAirborneHandOffset();
        Vector3 freeLeftHandPosition = new Vector3(-0.17f, 0.24f + airborneHandOffset + leftPhase * limbBobAmount * speed01, 0.1f + leftPhase * limbSwingAmount * speed01);
        Vector3 freeRightHandPosition = new Vector3(0.17f, 0.24f + airborneHandOffset + rightPhase * limbBobAmount * speed01, 0.1f + rightPhase * limbSwingAmount * speed01);

        if (handsAttached)
        {
            if (attachedHandsSnap)
            {
                if (leftHandAttached)
                {
                    SetWorld(leftHand, leftHandAnchor, attachedLeftHandRotation);
                }
                else
                {
                    MoveLocal(leftHand, freeLeftHandPosition, follow);
                }

                if (rightHandAttached)
                {
                    SetWorld(rightHand, rightHandAnchor, attachedRightHandRotation);
                }
                else
                {
                    MoveLocal(rightHand, freeRightHandPosition, follow);
                }

                return;
            }

            float gripFollow = 1f - Mathf.Exp(-handGripSharpness * deltaTime);
            if (leftHandAttached)
            {
                MoveWorld(leftHand, leftHandAnchor, gripFollow);
                RotateWorld(leftHand, attachedLeftHandRotation, gripFollow);
            }
            else
            {
                MoveLocal(leftHand, freeLeftHandPosition, follow);
            }

            if (rightHandAttached)
            {
                MoveWorld(rightHand, rightHandAnchor, gripFollow);
                RotateWorld(rightHand, attachedRightHandRotation, gripFollow);
            }
            else
            {
                MoveLocal(rightHand, freeRightHandPosition, follow);
            }

            return;
        }

        MoveLocal(leftHand, freeLeftHandPosition, follow);
        MoveLocal(rightHand, freeRightHandPosition, follow);
    }

    private float GetAirborneHandOffset()
    {
        if (!isAirborneAnimationActive)
        {
            return 0f;
        }

        return Mathf.Sin(Mathf.Clamp01(airAnimationTime) * Mathf.PI * 0.5f) * jumpHandLift;
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

    private void UpdateHeadLook(float deltaTime)
    {
        if (!driveHeadLookWithCamera)
        {
            return;
        }

        if (headLookNeedsAnimatorRefresh || headLookTransform == null)
        {
            ResolveHeadLookTransform();
            headLookNeedsAnimatorRefresh = characterModelRoot != null && !headLookHasPreferredTarget;
            if (headLookTransform == null)
            {
                return;
            }
        }
        else if (characterModelRoot != null && !headLookHasPreferredTarget)
        {
            Transform preferredTarget = FindPreferredHeadLookTransform(characterModelRoot);
            if (preferredTarget != null)
            {
                headLookTransform = preferredTarget;
                headLookHasPreferredTarget = true;
                headLookBaseLocalRotation = headLookTransform.localRotation;
            }
        }

        float follow = 1f - Mathf.Exp(-headLookSharpness * deltaTime);
        currentHeadLookPitch = Mathf.Lerp(currentHeadLookPitch, targetHeadLookPitch, follow);
        Vector3 axis = headLookLocalEulerAxis.sqrMagnitude > 0.0001f
            ? headLookLocalEulerAxis.normalized
            : Vector3.right;
        headLookTransform.localRotation = headLookBaseLocalRotation * Quaternion.Euler(axis * currentHeadLookPitch);
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

    private static Transform FindTransformByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || root.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindTransformByName(root.GetChild(i), name);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static Transform FindPreferredHeadLookTransform(Transform root)
    {
        Transform target = FindExactTransformByName(root, "BonesT\u00EAte");
        if (target != null)
        {
            return target;
        }

        target = FindExactTransformByName(root, "BonesTete");
        if (target != null)
        {
            return target;
        }

        target = FindNormalizedTransformByName(root, "BonesTete");
        return target != null ? target : FindTransformByPrefix(root, "BonesT");
    }

    private static Transform FindExactTransformByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindExactTransformByName(root.GetChild(i), name);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static Transform FindNormalizedTransformByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = NormalizeTransformName(name);
        if (NormalizeTransformName(root.name) == normalizedName)
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindNormalizedTransformByName(root.GetChild(i), name);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static Transform FindTransformByPrefix(Transform root, string prefix)
    {
        if (root == null || string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        if (root.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindTransformByPrefix(root.GetChild(i), prefix);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static string NormalizeTransformName(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormD);
        char[] buffer = new char[normalized.Length];
        int count = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(normalized[i]);
            if (category != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(normalized[i]))
            {
                buffer[count] = char.ToLowerInvariant(normalized[i]);
                count++;
            }
        }

        return new string(buffer, 0, count);
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

    private void DetachPhysicsPiece(Transform piece, int index)
    {
        if (piece == null)
        {
            return;
        }

        piece.SetParent(null, true);
        Collider collider = piece.GetComponent<Collider>();
        if (collider == null)
        {
            BoxCollider box = piece.gameObject.AddComponent<BoxCollider>();
            box.size = GetPieceLocalBoundsSize(piece);
            box.center = Vector3.zero;
            collider = box;
        }

        collider.enabled = true;
        Rigidbody body = piece.GetComponent<Rigidbody>();
        if (body == null)
        {
            body = piece.gameObject.AddComponent<Rigidbody>();
        }

        body.isKinematic = false;
        body.useGravity = true;
        body.mass = 0.2f;

        Vector3 outward = (piece.position - transform.position).normalized;
        if (outward.sqrMagnitude < 0.001f)
        {
            outward = Quaternion.Euler(0f, index * 51f, 0f) * Vector3.forward;
        }

        body.linearVelocity = outward * 0.8f + Vector3.up * 0.45f;
        body.angularVelocity = UnityEngine.Random.insideUnitSphere * 4f;
    }

    private static Vector3 GetPieceLocalBoundsSize(Transform piece)
    {
        Renderer[] renderers = piece.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return Vector3.one * 0.12f;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        Vector3 lossyScale = piece.lossyScale;
        return new Vector3(
            lossyScale.x != 0f ? Mathf.Max(0.03f, bounds.size.x / Mathf.Abs(lossyScale.x)) : 0.12f,
            lossyScale.y != 0f ? Mathf.Max(0.03f, bounds.size.y / Mathf.Abs(lossyScale.y)) : 0.12f,
            lossyScale.z != 0f ? Mathf.Max(0.03f, bounds.size.z / Mathf.Abs(lossyScale.z)) : 0.12f);
    }

    private IEnumerator DestroyOwnerAfterDelay(float delay)
    {
        if (delay > 0f)
        {
            yield return new WaitForSeconds(delay);
        }

        DestroyGameObject(gameObject);
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
