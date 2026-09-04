#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using UnityEngine.Scripting;

    public interface IOriginalSaveLoadPreparationService
    {
        OriginalSaveRuntimeSnapshot Prepare(string path);
    }

    public sealed class OriginalSaveLoadPreparationService : IOriginalSaveLoadPreparationService
    {
        private readonly IOriginalSaveRepository _repository;
        private readonly IOriginalSaveLoadPlanner _planner;
        private readonly IOriginalSaveRuntimeStager _stager;

        [Preserve]
        public OriginalSaveLoadPreparationService(
            IOriginalSaveRepository repository,
            IOriginalSaveLoadPlanner planner,
            IOriginalSaveRuntimeStager stager)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        }

        public OriginalSaveRuntimeSnapshot Prepare(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            OriginalSaveArchiveDocument document = _repository.Read(path);
            OriginalSaveLoadPlan plan = _planner.Prepare(document);
            return _stager.Stage(plan);
        }
    }
}
