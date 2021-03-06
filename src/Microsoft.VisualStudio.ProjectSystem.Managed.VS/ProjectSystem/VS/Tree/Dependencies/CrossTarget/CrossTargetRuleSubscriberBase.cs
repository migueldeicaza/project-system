﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using Microsoft.VisualStudio.ProjectSystem.LanguageServices;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.Utilities;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.CrossTarget
{
    internal abstract class CrossTargetRuleSubscriberBase<T> : OnceInitializedOnceDisposed, ICrossTargetSubscriber where T : IRuleChangeContext
    {
#pragma warning disable CA2213 // OnceInitializedOnceDisposedAsync are not tracked correctly by the IDisposeable analyzer
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(initialCount: 1);
        private DisposableBag _subscriptions;
#pragma warning restore CA2213
        private readonly IUnconfiguredProjectCommonServices _commonServices;
        private readonly IProjectAsynchronousTasksService _tasksService;
        private readonly IDependencyTreeTelemetryService _treeTelemetryService;
        private ICrossTargetSubscriptionsHost _host;
        private AggregateCrossTargetProjectContext _currentProjectContext;

        protected CrossTargetRuleSubscriberBase(
            IUnconfiguredProjectCommonServices commonServices,
            IProjectAsynchronousTasksService tasksService,
            IDependencyTreeTelemetryService treeTelemetryService)
            : base(synchronousDisposal: true)
        {
            _commonServices = commonServices;
            _tasksService = tasksService;
            _treeTelemetryService = treeTelemetryService;
        }

        protected abstract OrderPrecedenceImportCollection<ICrossTargetRuleHandler<T>> Handlers { get; }

        public void InitializeSubscriber(ICrossTargetSubscriptionsHost host, IProjectSubscriptionService subscriptionService)
        {
            _host = host;

            EnsureInitialized();

            IReadOnlyCollection<string> watchedEvaluationRules = GetWatchedRules(RuleHandlerType.Evaluation);
            IReadOnlyCollection<string> watchedDesignTimeBuildRules = GetWatchedRules(RuleHandlerType.DesignTimeBuild);

            SubscribeToConfiguredProject(
                _commonServices.ActiveConfiguredProject, subscriptionService, watchedEvaluationRules, watchedDesignTimeBuildRules);
        }

        public void AddSubscriptions(AggregateCrossTargetProjectContext newProjectContext)
        {
            Requires.NotNull(newProjectContext, nameof(newProjectContext));

            _currentProjectContext = newProjectContext;

            IReadOnlyCollection<string> watchedEvaluationRules = GetWatchedRules(RuleHandlerType.Evaluation);
            IReadOnlyCollection<string> watchedDesignTimeBuildRules = GetWatchedRules(RuleHandlerType.DesignTimeBuild);

            // initialize telemetry with all rules for each target framework
            foreach (ITargetedProjectContext projectContext in newProjectContext.InnerProjectContexts)
            {
                _treeTelemetryService.InitializeTargetFrameworkRules(projectContext.TargetFramework, watchedEvaluationRules);
                _treeTelemetryService.InitializeTargetFrameworkRules(projectContext.TargetFramework, watchedDesignTimeBuildRules);
            }

            foreach (ConfiguredProject configuredProject in newProjectContext.InnerConfiguredProjects)
            {
                SubscribeToConfiguredProject(
                    configuredProject, configuredProject.Services.ProjectSubscription, watchedEvaluationRules, watchedDesignTimeBuildRules);
            }
        }

        public void ReleaseSubscriptions()
        {
            _currentProjectContext = null;

            // We can't re-use the DisposableBag after disposing it, so null it out
            // to ensure we create a new one the next time we go to add subscriptions.
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        private void SubscribeToConfiguredProject(
            ConfiguredProject configuredProject,
            IProjectSubscriptionService subscriptionService,
            IReadOnlyCollection<string> watchedEvaluationRules,
            IReadOnlyCollection<string> watchedDesignTimeBuildRules)
        {
            // Use intermediate buffer blocks for project rule data to allow subsequent blocks
            // to only observe specific rule name(s).

            var intermediateBlockDesignTime =
                new BufferBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>(
                    new ExecutionDataflowBlockOptions()
                    {
                        NameFormat = "CrossTarget Intermediate DesignTime Input: {1}"
                    });

            var intermediateBlockEvaluation =
                new BufferBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>(
                    new ExecutionDataflowBlockOptions()
                    {
                        NameFormat = "CrossTarget Intermediate Evaluation Input: {1}"
                    });

            _subscriptions = _subscriptions ?? new DisposableBag();

            _subscriptions.AddDisposable(
                subscriptionService.JointRuleSource.SourceBlock.LinkTo(
                    intermediateBlockDesignTime,
                    ruleNames: watchedDesignTimeBuildRules.Union(watchedEvaluationRules),
                    suppressVersionOnlyUpdates: true,
                    linkOptions: DataflowOption.PropagateCompletion));

            _subscriptions.AddDisposable(
                subscriptionService.ProjectRuleSource.SourceBlock.LinkTo(
                    intermediateBlockEvaluation,
                    ruleNames: watchedEvaluationRules,
                    suppressVersionOnlyUpdates: true,
                    linkOptions: DataflowOption.PropagateCompletion));

            var actionBlockDesignTimeBuild =
                DataflowBlockSlim.CreateActionBlock<IProjectVersionedValue<Tuple<IProjectSubscriptionUpdate, IProjectCatalogSnapshot, IProjectCapabilitiesSnapshot>>>(
                    e => OnProjectChangedAsync(e.Value.Item1, e.Value.Item2, e.Value.Item3, configuredProject, RuleHandlerType.DesignTimeBuild),
                    new ExecutionDataflowBlockOptions()
                    {
                        NameFormat = "CrossTarget DesignTime Input: {1}"
                    });

            var actionBlockEvaluation =
                DataflowBlockSlim.CreateActionBlock<IProjectVersionedValue<Tuple<IProjectSubscriptionUpdate, IProjectCatalogSnapshot, IProjectCapabilitiesSnapshot>>>(
                     e => OnProjectChangedAsync(e.Value.Item1, e.Value.Item2, e.Value.Item3, configuredProject, RuleHandlerType.Evaluation),
                     new ExecutionDataflowBlockOptions()
                     {
                         NameFormat = "CrossTarget Evaluation Input: {1}"
                     });

            _subscriptions.AddDisposable(ProjectDataSources.SyncLinkTo(
                intermediateBlockDesignTime.SyncLinkOptions(),
                subscriptionService.ProjectCatalogSource.SourceBlock.SyncLinkOptions(),
                configuredProject.Capabilities.SourceBlock.SyncLinkOptions(),
                actionBlockDesignTimeBuild,
                linkOptions: DataflowOption.PropagateCompletion));

            _subscriptions.AddDisposable(ProjectDataSources.SyncLinkTo(
                intermediateBlockEvaluation.SyncLinkOptions(),
                subscriptionService.ProjectCatalogSource.SourceBlock.SyncLinkOptions(),
                configuredProject.Capabilities.SourceBlock.SyncLinkOptions(),
                actionBlockEvaluation,
                linkOptions: DataflowOption.PropagateCompletion));
        }

        private IReadOnlyCollection<string> GetWatchedRules(RuleHandlerType handlerType)
        {
            return new HashSet<string>(
                Handlers
                    .Where(h => h.Value.SupportsHandlerType(handlerType))
                    .SelectMany(h => h.Value.GetRuleNames(handlerType)),
                StringComparers.RuleNames);
        }

        private async Task OnProjectChangedAsync(
            IProjectSubscriptionUpdate projectUpdate,
            IProjectCatalogSnapshot catalogSnapshot,
            IProjectCapabilitiesSnapshot capabilities,
            ConfiguredProject configuredProject,
            RuleHandlerType handlerType)
        {
            if (IsDisposing || IsDisposed)
            {
                return;
            }

            await _tasksService.LoadedProjectAsync(async () =>
            {
                if (_tasksService.UnloadCancellationToken.IsCancellationRequested)
                {
                    return;
                }

                using (ProjectCapabilitiesContext.CreateIsolatedContext(configuredProject, capabilities))
                {
                    await HandleAsync(projectUpdate, catalogSnapshot, handlerType);
                }
            });
        }

        private async Task HandleAsync(
            IProjectSubscriptionUpdate projectUpdate,
            IProjectCatalogSnapshot catalogSnapshot,
            RuleHandlerType handlerType)
        {
            AggregateCrossTargetProjectContext currentAggregateContext = await _host.GetCurrentAggregateProjectContext();
            if (currentAggregateContext == null || _currentProjectContext != currentAggregateContext)
            {
                return;
            }

            IEnumerable<ICrossTargetRuleHandler<T>> handlers 
                = Handlers
                    .Select(h => h.Value)
                    .Where(h => h.SupportsHandlerType(handlerType));

            ITargetedProjectContext projectContextToUpdate;

            // We need to process the update within a lock to ensure that we do not release this context during processing.
            // TODO: Enable concurrent execution of updates themselves, i.e. two separate invocations of HandleAsync
            //       should be able to run concurrently.
            using (await _gate.DisposableWaitAsync())
            {
                // Get the inner workspace project context to update for this change.
                projectContextToUpdate = currentAggregateContext
                    .GetInnerProjectContext(projectUpdate.ProjectConfiguration, out bool isActiveContext);

                if (projectContextToUpdate == null)
                {
                    return;
                }

                // Broken design time builds sometimes cause updates with no project changes and sometimes
                // cause updates with a project change that has no difference.
                // We handle the former case here, and the latter case is handled in the CommandLineItemHandler.
                if (projectUpdate.ProjectChanges.Count == 0)
                {
                    if (handlerType == RuleHandlerType.DesignTimeBuild)
                    {
                        projectContextToUpdate.LastDesignTimeBuildSucceeded = false;
                    }

                    return;
                }

                // Get the subclass-specific context that will aggregate data during the processing of rule data by handlers.
                T ruleChangeContext = CreateRuleChangeContext(
                    currentAggregateContext.ActiveProjectContext.TargetFramework,
                    catalogSnapshot);

                // Give each handler a chance to modify the rule change context.
                foreach (ICrossTargetRuleHandler<T> handler in handlers)
                {
                    ImmutableHashSet<string> handlerRules = handler.GetRuleNames(handlerType);

                    // Slice project changes to include only rules the handler claims an interest in.
                    var projectChanges = projectUpdate.ProjectChanges
                        .Where(x => handlerRules.Contains(x.Key))
                        .ToImmutableDictionary();

                    if (handler.ReceiveUpdatesWithEmptyProjectChange
                        || projectChanges.Any(x => x.Value.Difference.AnyChanges))
                    {
                        // Handlers respond to rule changes in a way that's specific to the rule change context
                        // type (T). For example, DependencyRulesSubscriber uses DependenciesRuleChangeContext
                        // which holds IDependencyModel, so its ICrossTargetRuleHandler<DependenciesRuleChangeContext>
                        // implementations will produce IDependencyModel objects in response to rule changes.
                        handler.Handle(projectChanges, projectContextToUpdate.TargetFramework, ruleChangeContext);
                    }
                }

                // Notify the subclass that their rule change context object is ready for finalization.
                CompleteHandle(ruleChangeContext);
            }

            // record all the rules that have occurred
            _treeTelemetryService.ObserveTargetFrameworkRules(projectContextToUpdate.TargetFramework, projectUpdate.ProjectChanges.Keys);
        }

        protected abstract T CreateRuleChangeContext(ITargetFramework activeTarget, IProjectCatalogSnapshot catalogs);

        protected virtual void CompleteHandle(T ruleChangeContext)
        {
        }

        protected override void Initialize()
        {   
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseSubscriptions();
            }
        }
    }
}
