using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Octopus.Cli.Infrastructure;
using Octopus.Cli.Model;
using Octopus.Cli.Util;
using Octopus.Client;
using Octopus.Client.Model;
using Serilog;

namespace Octopus.Cli.Commands.Releases
{
    public class ReleasePlanBuilder : IReleasePlanBuilder
    {
        private readonly IPackageVersionResolver versionResolver;
        private readonly IChannelVersionRuleTester versionRuleTester;
        private readonly ICommandOutputProvider commandOutputProvider;

        public ReleasePlanBuilder(ILogger log, IPackageVersionResolver versionResolver, IChannelVersionRuleTester versionRuleTester, ICommandOutputProvider commandOutputProvider)
        {
            this.versionResolver = versionResolver;
            this.versionRuleTester = versionRuleTester;
            this.commandOutputProvider = commandOutputProvider;
        }

        public async Task<ReleasePlan> Build(IOctopusAsyncRepository repository, ProjectResource project, ChannelResource channel, string versionPreReleaseTag, string versionPreReleaseTagFallBacks, bool LatestByPublishDate)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (project == null) throw new ArgumentNullException(nameof(project));

            commandOutputProvider.Debug("Finding deployment process...");
            var deploymentProcess = await repository.DeploymentProcesses.Get(project.DeploymentProcessId).ConfigureAwait(false);

            commandOutputProvider.Debug("Finding release template...");
            var releaseTemplate = await repository.DeploymentProcesses.GetTemplate(deploymentProcess, channel).ConfigureAwait(false);

            var plan = new ReleasePlan(project, channel, releaseTemplate, deploymentProcess, versionResolver);

            if (plan.UnresolvedSteps.Any())
            {
                commandOutputProvider.Debug("The package version for some steps was not specified. Going to try and resolve those automatically...");
                
                foreach (var unresolved in plan.UnresolvedSteps)
                {
                    if (!unresolved.IsResolveable)
                    {
                        commandOutputProvider.Error("The version number for step '{Step:l}' cannot be automatically resolved because the feed or package ID is dynamic.", unresolved.ActionName);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(versionPreReleaseTag))
                        commandOutputProvider.Debug("\r\n\r\nFinding latest package with pre-release '{Tag:l}' for step: {StepName:l}", versionPreReleaseTag, unresolved.ActionName);
                    else
                        commandOutputProvider.Debug("\r\n\r\nFinding latest package for step: {StepName:l}", unresolved.ActionName);

                    var feed = await repository.Feeds.Get(unresolved.PackageFeedId).ConfigureAwait(false);
                    if (feed == null)
                        throw new CommandException(string.Format("Could not find a feed with ID {0}, which is used by step: " + unresolved.ActionName, unresolved.PackageFeedId));

                    var filters = BuildChannelVersionFilters(unresolved.ActionName, unresolved.PackageReferenceName, channel);
                    filters["packageId"] = unresolved.PackageId;

                    if (!string.IsNullOrWhiteSpace(versionPreReleaseTag))
                        filters["preReleaseTag"] = versionPreReleaseTag;

                    var packages = await repository.Client.Get<List<PackageResource>>(feed.Link("SearchTemplate"), filters).ConfigureAwait(false);
                    PackageResource latestPackage;

                    
                    bool NotEmptyPreReleaseTag = !(string.IsNullOrWhiteSpace(versionPreReleaseTag) || versionPreReleaseTag == "^$");

                    //Get the latest published package for release instead of package has the biggest SemVer
                    //Only for pre-release packages and only if LatestByPublishDate prop specified
                    //Using latest published package is inappropriate for release packages, because hotfix releases for old versions may be pushed after main major version.
                    if (LatestByPublishDate && NotEmptyPreReleaseTag) {
                        latestPackage = packages.OrderByDescending(o => o.Published).FirstOrDefault();
                        if (latestPackage != null) { commandOutputProvider.Debug("'--latestbypublishdate' flag was specified. Package resolver will choose version of package '{PackageId:l}' by the latest publishing date instead of the higest SemVer version.", unresolved.ActionName, latestPackage.PackageId); }
                    } else {
                        latestPackage = packages.FirstOrDefault();
                    }



                    if (latestPackage == null && !string.IsNullOrWhiteSpace(versionPreReleaseTag) && !string.IsNullOrWhiteSpace(versionPreReleaseTagFallBacks)) {
                        commandOutputProvider.Debug("Could not find latest package with pre-release '{Tag:l}' for step: {StepName:l}, falling back to search with pre-release tags '{FallBackTags:l}' ", versionPreReleaseTag, unresolved.ActionName, versionPreReleaseTagFallBacks);
                        List<string> versionPreReleaseTagFallBacksList = versionPreReleaseTagFallBacks.Split(',').ToList().Select(s => s.Trim()).ToList();
                        foreach (string versionPreReleaseTagFallBack in versionPreReleaseTagFallBacksList) {
                            filters["preReleaseTag"] = versionPreReleaseTagFallBack;

                            packages = await repository.Client.Get<List<PackageResource>>(feed.Link("SearchTemplate"), filters).ConfigureAwait(false);


                            bool NotEmptyPreReleaseTagFallBack = !(string.IsNullOrWhiteSpace(versionPreReleaseTagFallBack) || versionPreReleaseTagFallBack == "^$");
                            //same beahaviour as for general versionPreReleaseTag
                            if (LatestByPublishDate && NotEmptyPreReleaseTagFallBack)
                            {
                                latestPackage = packages.OrderByDescending(o => o.Published).FirstOrDefault();
                                if (latestPackage != null) { commandOutputProvider.Debug("'--latestbypublishdate' flag was specified. Package resolver will choose version of package '{PackageId:l}' by the latest publishing date instead of the higest SemVer version.", unresolved.ActionName, latestPackage.PackageId); }
                            }
                           
                            else
                            {
                                latestPackage = packages.FirstOrDefault();
                            }
                            
                            if (latestPackage != null) { break; }
                        }
                        

                    }


                    if (latestPackage == null)
                    {
                        commandOutputProvider.Error("Could not find any packages with ID '{PackageId:l}' in the feed '{FeedUri:l}'", unresolved.PackageId, feed.Name);
                    }
                    else
                    {
                        commandOutputProvider.Debug("Selected '{PackageId:l}' version '{Version:l}' for '{StepName:l}'", latestPackage.PackageId, latestPackage.Version, unresolved.ActionName);
                        unresolved.SetVersionFromLatest(latestPackage.Version);
                    }
                }
            }

            // Test each step in this plan satisfies the channel version rules
            if (channel != null)
            {
                foreach (var step in plan.PackageSteps)
                {
                    // Note the rule can be null, meaning: anything goes
                    var rule = channel.Rules.SingleOrDefault(r => r.ActionPackages.Any(pkg => pkg.DeploymentActionNameMatches(step.ActionName) && pkg.PackageReferenceNameMatches(step.PackageReferenceName)));
                    var result = await versionRuleTester.Test(repository, rule, step.Version).ConfigureAwait(false);
                    step.SetChannelVersionRuleTestResult(result);
                }
            }

            return plan;
        }

        IDictionary<string, object> BuildChannelVersionFilters(string stepName, string packageReferenceName, ChannelResource channel)
        {
            var filters = new Dictionary<string, object>();

            if (channel == null)
                return filters;

            var rule = channel.Rules.FirstOrDefault(r => r.ActionPackages.Any(pkg => pkg.DeploymentActionNameMatches(stepName) && pkg.PackageReferenceNameMatches(packageReferenceName)));
            
            if (rule == null)
                return filters;

            if (!string.IsNullOrWhiteSpace(rule.VersionRange))
                filters["versionRange"] = rule.VersionRange;

            if (!string.IsNullOrWhiteSpace(rule.Tag))
                filters["preReleaseTag"] = rule.Tag;

            return filters;
        }
    }
}