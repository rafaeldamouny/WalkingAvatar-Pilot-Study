using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Shows the saved corridor avatar while the experiment menu is open, then hides it
    /// once the live trial avatar takes over. This keeps the hallway preview from
    /// appearing as a second participant during trials.
    /// </summary>
    public sealed class CorridorAvatarPreviewToggle : MonoBehaviour
    {
        public SeatedWalkingPlatform platform;
        public GameObject previewAvatar;

        void LateUpdate()
        {
            bool shouldShow = !platform || !platform.ExperimentStarted;
            if (previewAvatar && previewAvatar.activeSelf != shouldShow)
                previewAvatar.SetActive(shouldShow);
        }
    }
}
