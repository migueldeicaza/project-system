﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.CrossTarget;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot.Filters;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot
{
    internal sealed class TargetedDependenciesSnapshot : ITargetedDependenciesSnapshot
    {
        #region Factories and internal constructor

        public static ITargetedDependenciesSnapshot CreateEmpty(string projectPath, ITargetFramework targetFramework, IProjectCatalogSnapshot catalogs)
        {
            return new TargetedDependenciesSnapshot(
                projectPath,
                targetFramework,
                catalogs,
                ImmutableStringDictionary<IDependency>.EmptyOrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies changes to <paramref name="previousSnapshot"/> and produces a new snapshot if required.
        /// If no changes are made, <paramref name="previousSnapshot"/> is returned unmodified.
        /// </summary>
        /// <returns>An updated snapshot, or <paramref name="previousSnapshot"/> if no changes occured.</returns>
        public static ITargetedDependenciesSnapshot FromChanges(
            string projectPath,
            ITargetedDependenciesSnapshot previousSnapshot,
            IDependenciesChanges changes,
            IProjectCatalogSnapshot catalogs,
            IReadOnlyList<IDependenciesSnapshotFilter> snapshotFilters,
            IReadOnlyDictionary<string, IProjectDependenciesSubTreeProvider> subTreeProviderByProviderType,
            IImmutableSet<string> projectItemSpecs)
        {
            Requires.NotNullOrWhiteSpace(projectPath, nameof(projectPath));
            Requires.NotNull(previousSnapshot, nameof(previousSnapshot));
            // catalogs can be null
            Requires.NotNull(changes, nameof(changes));
            Requires.NotNull(snapshotFilters, nameof(snapshotFilters));
            Requires.NotNull(subTreeProviderByProviderType, nameof(subTreeProviderByProviderType));
            // projectItemSpecs can be null

            bool anyChanges = false;

            ITargetFramework targetFramework = previousSnapshot.TargetFramework;

            var worldBuilder = previousSnapshot.DependenciesWorld.ToBuilder();

            foreach (IDependencyModel removedNode in changes.RemovedNodes)
            {
                string targetedId = Dependency.GetID(targetFramework, removedNode.ProviderType, removedNode.Id);

                if (!worldBuilder.TryGetValue(targetedId, out IDependency dependency))
                {
                    continue;
                }

                bool canRemove = true;

                if (snapshotFilters != null)
                {
                    foreach (IDependenciesSnapshotFilter filter in snapshotFilters)
                    {
                        canRemove = filter.BeforeRemove(
                            projectPath, targetFramework, dependency, worldBuilder, out bool filterAnyChanges);

                        anyChanges |= filterAnyChanges;

                        if (!canRemove)
                        {
                            // TODO breaking here denies later filters the opportunity to modify builders
                            break;
                        }
                    }
                }

                if (canRemove)
                {
                    anyChanges = true;
                    worldBuilder.Remove(targetedId);
                }
            }

            foreach (IDependencyModel added in changes.AddedNodes)
            {
                IDependency newDependency = new Dependency(added, targetFramework, projectPath);

                if (snapshotFilters != null)
                {
                    foreach (IDependenciesSnapshotFilter filter in snapshotFilters)
                    {
                        newDependency = filter.BeforeAdd(
                            projectPath,
                            targetFramework,
                            newDependency,
                            worldBuilder,
                            subTreeProviderByProviderType,
                            projectItemSpecs,
                            out bool filterAnyChanges);

                        anyChanges |= filterAnyChanges;

                        if (newDependency == null)
                        {
                            break;
                        }
                    }
                }

                if (newDependency == null)
                {
                    continue;
                }

                anyChanges = true;

                worldBuilder.Remove(newDependency.Id);
                worldBuilder.Add(newDependency.Id, newDependency);
            }

            // Also factor in any changes to path/framework/catalogs
            anyChanges =
                anyChanges ||
                !StringComparers.Paths.Equals(projectPath, previousSnapshot.ProjectPath) ||
                !targetFramework.Equals(previousSnapshot.TargetFramework) ||
                !Equals(catalogs, previousSnapshot.Catalogs);

            if (anyChanges)
            {
                return new TargetedDependenciesSnapshot(
                    projectPath,
                    targetFramework,
                    catalogs,
                    worldBuilder.ToImmutable());
            }

            return previousSnapshot;
        }

        // Internal, for test use -- normal code should use the factory methods
        internal TargetedDependenciesSnapshot(
            string projectPath,
            ITargetFramework targetFramework,
            IProjectCatalogSnapshot catalogs,
            ImmutableDictionary<string, IDependency> dependenciesWorld)
        {
            Requires.NotNullOrEmpty(projectPath, nameof(projectPath));
            Requires.NotNull(targetFramework, nameof(targetFramework));
            // catalogs can be null
            Requires.NotNull(dependenciesWorld, nameof(dependenciesWorld));

            ProjectPath = projectPath;
            TargetFramework = targetFramework;
            Catalogs = catalogs;
            DependenciesWorld = dependenciesWorld;

            bool hasUnresolvedDependency = false;
            ImmutableHashSet<IDependency>.Builder topLevelDependencies = ImmutableHashSet.CreateBuilder<IDependency>();

            foreach ((string id, IDependency dependency) in dependenciesWorld)
            {
                System.Diagnostics.Debug.Assert(
                    string.Equals(id, dependency.Id),
                    "dependenciesWorld dictionary entry keys must match their value's ids.");

                if (!dependency.Resolved)
                {
                    hasUnresolvedDependency = true;
                }

                if (dependency.TopLevel)
                {
                    bool added = topLevelDependencies.Add(dependency);
                    System.Diagnostics.Debug.Assert(added, "Duplicate top level dependency found.");

                    if (!string.IsNullOrEmpty(dependency.Path))
                    {
                        _topLevelDependenciesByPathMap.Add(
                            Dependency.GetID(TargetFramework, dependency.ProviderType, dependency.Path),
                            dependency);
                    }
                }
            }

            HasUnresolvedDependency = hasUnresolvedDependency;
            TopLevelDependencies = topLevelDependencies.ToImmutable();
        }

        #endregion

        /// <inheritdoc />
        public string ProjectPath { get; }

        /// <inheritdoc />
        public ITargetFramework TargetFramework { get; }

        /// <inheritdoc />
        public IProjectCatalogSnapshot Catalogs { get; }

        /// <inheritdoc />
        public ImmutableHashSet<IDependency> TopLevelDependencies { get; }

        /// <inheritdoc />
        public ImmutableDictionary<string, IDependency> DependenciesWorld { get; }

        private readonly Dictionary<string, IDependency> _topLevelDependenciesByPathMap = new Dictionary<string, IDependency>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImmutableArray<IDependency>> _dependenciesChildrenMap = new Dictionary<string, ImmutableArray<IDependency>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _unresolvedDescendantsMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Re-use an existing, private, object reference for locking, rather than allocating a dedicated object.</summary>
        private object SyncLock => _dependenciesChildrenMap;

        /// <inheritdoc />
        public bool HasUnresolvedDependency { get; }

        /// <inheritdoc />
        public bool CheckForUnresolvedDependencies(IDependency dependency)
        {
            lock (SyncLock)
            {
                if (!_unresolvedDescendantsMap.TryGetValue(dependency.Id, out bool unresolved))
                {
                    unresolved = _unresolvedDescendantsMap[dependency.Id] = FindUnresolvedDependenciesRecursive(dependency);
                }

                return unresolved;
            }

            bool FindUnresolvedDependenciesRecursive(IDependency parent)
            {
                if (parent.DependencyIDs.Count == 0)
                {
                    return false;
                }

                foreach (IDependency child in GetDependencyChildren(parent))
                {
                    if (!child.Resolved)
                    {
                        return true;
                    }

                    // If the dependency is already in the child map, it is resolved
                    // Checking here will prevent a stack overflow due to rechecking the same dependencies
                    if (_dependenciesChildrenMap.ContainsKey(child.Id))
                    {
                        return false;
                    }

                    if (!_unresolvedDescendantsMap.TryGetValue(child.Id, out bool depthFirstResult))
                    {
                        depthFirstResult = FindUnresolvedDependenciesRecursive(child);
                        _unresolvedDescendantsMap[parent.Id] = depthFirstResult;
                        return depthFirstResult;
                    }

                    if (depthFirstResult)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <inheritdoc />
        public bool CheckForUnresolvedDependencies(string providerType)
        {
            foreach ((string _, IDependency dependency) in DependenciesWorld)
            {
                if (StringComparers.DependencyProviderTypes.Equals(dependency.ProviderType, providerType) && 
                    !dependency.Resolved)
                {
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public ImmutableArray<IDependency> GetDependencyChildren(IDependency dependency)
        {
            if (dependency.DependencyIDs.Count == 0)
            {
                return ImmutableArray<IDependency>.Empty;
            }

            lock (SyncLock)
            {
                if (!_dependenciesChildrenMap.TryGetValue(dependency.Id, out ImmutableArray<IDependency> children))
                {
                    children = _dependenciesChildrenMap[dependency.Id] = BuildChildren();
                }

                return children;
            }

            ImmutableArray<IDependency> BuildChildren()
            {
                ImmutableArray<IDependency>.Builder children =
                    ImmutableArray.CreateBuilder<IDependency>(dependency.DependencyIDs.Count);

                foreach (string id in dependency.DependencyIDs)
                {
                    if (DependenciesWorld.TryGetValue(id, out IDependency child) ||
                        _topLevelDependenciesByPathMap.TryGetValue(id, out child))
                    {
                        children.Add(child);
                    }
                }

                return children.Count == children.Capacity
                    ? children.MoveToImmutable()
                    : children.ToImmutable();
            }
        }
    }
}
