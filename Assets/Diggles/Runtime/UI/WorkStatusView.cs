#nullable enable

namespace Diggles.Runtime.UI
{
    using UnityEngine;
    using UnityEngine.Serialization;

    public sealed class WorkStatusView : MonoBehaviour
    {
        [FormerlySerializedAs("worktimeWork")]
        [SerializeField] GameObject? worktimeWithJob;
        [FormerlySerializedAs("worktimeNoWork")]
        [SerializeField] GameObject? worktimeWithoutJob;
        [FormerlySerializedAs("sparetime")]
        [SerializeField] GameObject? freetime;
        [FormerlySerializedAs("manual")]
        [SerializeField] GameObject? manualMode;

        bool warnedMissing;
        DiggleWorkStatus shownStatus = (DiggleWorkStatus)(-1);

        void Awake() => ResolveReferences();

        public void SetStatus(DiggleWorkStatus status)
        {
            ResolveReferences();
            if (shownStatus == status) return;
            shownStatus = status;
            SetActive(worktimeWithJob, status == DiggleWorkStatus.WorktimeWithJob);
            SetActive(worktimeWithoutJob, status == DiggleWorkStatus.WorktimeWithoutJob);
            SetActive(freetime, status == DiggleWorkStatus.Freetime);
            SetActive(manualMode, status == DiggleWorkStatus.ManualMode);
        }

        void ResolveReferences()
        {
            if (worktimeWithJob == null)
                worktimeWithJob = DiggleUiBindings.FindDeep(transform, "Worktime_work")?.gameObject;
            if (worktimeWithoutJob == null)
                worktimeWithoutJob = DiggleUiBindings.FindDeep(transform, "Worktime_nowork")?.gameObject;
            if (freetime == null)
                freetime = DiggleUiBindings.FindDeep(transform, "Sparetime")?.gameObject;
            if (manualMode == null)
                manualMode = DiggleUiBindings.FindDeep(transform, "Manual")?.gameObject;

            if (worktimeWithJob == null || worktimeWithoutJob == null
                || freetime == null || manualMode == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "WorkStatusView is missing one or more authored status objects; available states will still update.");
        }

        static void SetActive(GameObject? target, bool active)
        {
            if (target != null && target.activeSelf != active) target.SetActive(active);
        }
    }
}
