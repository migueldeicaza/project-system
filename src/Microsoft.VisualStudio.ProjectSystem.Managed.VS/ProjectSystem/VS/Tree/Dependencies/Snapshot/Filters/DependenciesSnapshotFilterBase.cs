﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.CrossTarget;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot.Filters
{
    /// <summary>
    /// Base class for all snapshot filters.
    /// </summary>
    internal abstract class DependenciesSnapshotFilterBase : IDependenciesSnapshotFilter
    {
        public virtual IDependency BeforeAdd(
            string projectPath,
            ITargetFramework targetFramework,
            IDependency dependency,
            ImmutableDictionary<string, IDependency>.Builder worldBuilder,
            IReadOnlyDictionary<string, IProjectDependenciesSubTreeProvider> subTreeProviderByProviderType,
            IImmutableSet<string> projectItemSpecs,
            out bool filterAnyChanges)
        {
            filterAnyChanges = false;
            return dependency;
        }

        public virtual bool BeforeRemove(
            string projectPath,
            ITargetFramework targetFramework,
            IDependency dependency,
            ImmutableDictionary<string, IDependency>.Builder worldBuilder,
            out bool filterAnyChanges)
        {
            filterAnyChanges = false;
            return true;
        }
    }
}
