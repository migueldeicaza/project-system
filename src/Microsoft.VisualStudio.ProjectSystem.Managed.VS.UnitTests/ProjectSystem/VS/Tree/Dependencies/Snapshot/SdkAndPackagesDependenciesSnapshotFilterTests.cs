﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;

using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.CrossTarget;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot.Filters;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Subscriptions;

using Moq;

using Xunit;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies
{
    public class SdkAndPackagesDependenciesSnapshotFilterTests
    {
        [Fact]
        public void WhenNotTopLevelOrResolved_ShouldDoNothing()
        {
            var dependency = IDependencyFactory.Implement(
                id: "mydependency1",
                topLevel: false);

            var worldBuilder = new [] { dependency.Object }.ToImmutableDictionary(d => d.Id).ToBuilder();

            var filter = new SdkAndPackagesDependenciesSnapshotFilter();

            var resultDependency = filter.BeforeAdd(
                null,
                null,
                dependency.Object,
                worldBuilder,
                null,
                null,
                out bool filterAnyChanges);

            dependency.VerifyAll();
        }

        [Fact]
        public void WhenSdk_ShouldFindMatchingPackageAndSetProperties()
        {
            var dependencyIDs = new [] { "id1", "id2" }.ToImmutableList();

            var mockTargetFramework = ITargetFrameworkFactory.Implement(moniker: "tfm");

            var flags = DependencyTreeFlags.SdkSubTreeNodeFlags
                               .Union(DependencyTreeFlags.ResolvedFlags)
                                .Except(DependencyTreeFlags.UnresolvedFlags);
            var sdkDependency = IDependencyFactory.Implement(
                flags: DependencyTreeFlags.SdkSubTreeNodeFlags,
                id: "mydependency1id",
                name: "mydependency1",
                topLevel: true,
                setPropertiesDependencyIDs: dependencyIDs,
                setPropertiesResolved: true,
                setPropertiesSchemaName: ResolvedSdkReference.SchemaName,
                setPropertiesFlags: flags);

            var otherDependency = IDependencyFactory.Implement(
                    id: $"tfm\\{PackageRuleHandler.ProviderTypeString}\\mydependency1",
                    resolved: true,
                    dependencyIDs: dependencyIDs);

            var worldBuilder = new[] { sdkDependency.Object, otherDependency.Object }.ToImmutableDictionary(d => d.Id).ToBuilder();

            var filter = new SdkAndPackagesDependenciesSnapshotFilter();

            var resultDependency = filter.BeforeAdd(
                null,
                mockTargetFramework,
                sdkDependency.Object,
                worldBuilder,
                null,
                null,
                out bool filterAnyChanges);

            sdkDependency.VerifyAll();
            otherDependency.VerifyAll();
        }

        [Fact]
        public void WhenSdkAndPackageUnresolved_ShouldDoNothing()
        {
            var mockTargetFramework = ITargetFrameworkFactory.Implement(moniker: "tfm");

            var dependency = IDependencyFactory.Implement(
                flags: DependencyTreeFlags.SdkSubTreeNodeFlags,
                id: "mydependency1id",
                name: "mydependency1",
                topLevel: true);

            var otherDependency = IDependencyFactory.Implement(
                    id: $"tfm\\{PackageRuleHandler.ProviderTypeString}\\mydependency1",
                    resolved: false);

            var worldBuilder = new[] { dependency.Object, otherDependency.Object }.ToImmutableDictionary(d => d.Id).ToBuilder();

            var filter = new SdkAndPackagesDependenciesSnapshotFilter();

            var resultDependency = filter.BeforeAdd(
                null,
                mockTargetFramework,
                dependency.Object,
                worldBuilder,
                null,
                null,
                out bool filterAnyChanges);

            dependency.VerifyAll();
            otherDependency.VerifyAll();
        }

        [Fact]
        public void WhenPackage_ShouldFindMatchingSdkAndSetProperties()
        {
            var dependencyIDs = new [] { "id1", "id2" }.ToImmutableList();

            var mockTargetFramework = ITargetFrameworkFactory.Implement(moniker: "tfm");

            var dependency = IDependencyFactory.Implement(
                id: "mydependency1id",
                flags: DependencyTreeFlags.PackageNodeFlags,
                name: "mydependency1",
                topLevel: true,
                resolved: true,
                dependencyIDs: dependencyIDs);

            var flags = DependencyTreeFlags.PackageNodeFlags
                                           .Union(DependencyTreeFlags.ResolvedFlags)
                                           .Except(DependencyTreeFlags.UnresolvedFlags);
            var sdkDependency = IDependencyFactory.Implement(
                    id: $"tfm\\{SdkRuleHandler.ProviderTypeString}\\mydependency1",
                    flags: DependencyTreeFlags.PackageNodeFlags.Union(DependencyTreeFlags.UnresolvedFlags), // to see if unresolved is fixed
                    setPropertiesResolved: true,
                    setPropertiesDependencyIDs: dependencyIDs,
                    setPropertiesFlags: flags,
                    setPropertiesSchemaName: ResolvedSdkReference.SchemaName,
                    equals: true);

            var worldBuilder = new[] { dependency.Object, sdkDependency.Object }.ToImmutableDictionary(d => d.Id).ToBuilder();

            var filter = new SdkAndPackagesDependenciesSnapshotFilter();

            var resultDependency = filter.BeforeAdd(
                null,
                mockTargetFramework,
                dependency.Object,
                worldBuilder,
                null,
                null,
                out bool filterAnyChanges);

            dependency.VerifyAll();
            sdkDependency.VerifyAll();
        }

        [Fact]
        public void WhenPackageRemoving_ShouldCleanupSdk()
        {
            var resolvedFlags = DependencyTreeFlags.SdkSubTreeNodeFlags.Union(DependencyTreeFlags.ResolvedFlags);
            var unresolvedFlags = DependencyTreeFlags.SdkSubTreeNodeFlags.Union(DependencyTreeFlags.UnresolvedFlags).Except(DependencyTreeFlags.ResolvedFlags);

            var targetFramework = new TargetFramework("tfm");

            var packageName = "mydependency1";

            var packageDependency = IDependencyFactory.Implement(
                id: "mydependency1id",
                flags: DependencyTreeFlags.PackageNodeFlags,
                name: packageName,
                topLevel: true,
                resolved: true);

            var modifiedSdkDependency = IDependencyFactory.Implement().Object;

            var sdkDependency = IDependencyFactory.Implement(
                id: $"{targetFramework.ShortName}\\{SdkRuleHandler.ProviderTypeString}\\{packageName}",
                flags: resolvedFlags,
                setPropertiesDependencyIDs: ImmutableList<string>.Empty,
                setPropertiesResolved: false,
                setPropertiesSchemaName: SdkReference.SchemaName,
                setPropertiesFlags: unresolvedFlags,
                setPropertiesReturn: modifiedSdkDependency,
                mockBehavior: MockBehavior.Loose);

            var worldBuilder = new[] { packageDependency.Object, sdkDependency.Object }.ToImmutableDictionary(d => d.Id).ToBuilder();

            var filter = new SdkAndPackagesDependenciesSnapshotFilter();

            Assert.True(filter.BeforeRemove(
                projectPath: null,
                targetFramework,
                packageDependency.Object,
                worldBuilder,
                out bool filterAnyChanges));

            packageDependency.VerifyAll();
            sdkDependency.VerifyAll();

            Assert.True(filterAnyChanges);

            Assert.True(worldBuilder.TryGetValue(packageDependency.Object.Id, out var afterPackageDependency));
            Assert.Same(packageDependency.Object, afterPackageDependency);

            Assert.True(worldBuilder.TryGetValue(sdkDependency.Object.Id, out var afterSdkDependency));
            Assert.Same(modifiedSdkDependency, afterSdkDependency);
        }
    }
}
