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

        public async Task<ReleasePlan> Build(IOctopusAsyncRepository repository, ProjectResource project, ChannelResource channel, string versionPreReleaseTag, string versionPreReleaseTagFallBacks, string softDefaultPackageVersion, bool LatestByPublishDate)
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


                    var feed = await repository.Feeds.Get(unresolved.PackageFeedId).ConfigureAwait(false);
                    if (feed == null)
                        throw new CommandException(string.Format("Could not find a feed with ID {0}, which is used by step: " + unresolved.ActionName, unresolved.PackageFeedId));
                    var packages = new System.Collections.Generic.List<Octopus.Client.Model.PackageResource>();
                    PackageResource latestPackage;
                    String versionSource = "Latest available";

                    var filters = BuildChannelVersionFilters(unresolved.ActionName, unresolved.PackageReferenceName, channel);
                    filters["packageId"] = unresolved.PackageId;
                    
                    commandOutputProvider.Debug("------------------------------------------\r\n");
                    commandOutputProvider.Debug("Step: '{StepName:l}', Package: '{PackageId:l}':\r\n", unresolved.ActionName, unresolved.PackageId);


                    //look for exact version of package specified softDefaultPackageVersion, bypass all further version-seeking heurstics if succeed
                    if (!string.IsNullOrWhiteSpace(softDefaultPackageVersion)) {
                        filters["versionRange"] = "[" + softDefaultPackageVersion + "]";
                        packages = await repository.Client.Get<List<PackageResource>>(feed.Link("SearchTemplate"), filters).ConfigureAwait(false);
                        latestPackage = packages.FirstOrDefault();
                        if (latestPackage != null)
                        {
                            commandOutputProvider.Debug("Version '{Version:l}' was found using softDefaultPackageVersion specified. Any further version-seeking heurstics will be bypassed.", latestPackage.Version);
                            versionSource = "softDefaultPackageVersion";
                            unresolved.SetVersionFromLatest(latestPackage.Version, versionSource);
                            continue;
                        }
                        else { 
                            filters.Remove("versionRange");
                            commandOutputProvider.Debug("Could not find package with softDefaultPackageVersion: '{softDefaultPackageVersion:l}', falling back to search with another specified methods (versionPreReleaseTag,versionPreReleaseTagFallBacks)", softDefaultPackageVersion);
                        }

                    }
                    



                    if (!string.IsNullOrWhiteSpace(versionPreReleaseTag))
                    {
                        filters["preReleaseTag"] = versionPreReleaseTag;
                        versionSource = "versionPreReleaseTag";
                        commandOutputProvider.Debug("versionPreReleaseTag: '{Tag:l}' was specified. Looking for latest package with this tag.'", versionPreReleaseTag);
                    }



                    bool ResolverLooksForPreReleasePackage = !(string.IsNullOrWhiteSpace(versionPreReleaseTag) || versionPreReleaseTag == "^$");


                    //As we can't sort by publishing date on the server side, we have to take all packages and sort them on the client side
                    if (LatestByPublishDate && ResolverLooksForPreReleasePackage)
                        filters["take"] = 10000;


                    packages = await repository.Client.Get<List<PackageResource>>(feed.Link("SearchTemplate"), filters).ConfigureAwait(false);


                    
                    //Get the latest published package for release instead of the package has the biggest SemVer
                    //Only for pre-release packages and only if LatestByPublishDate prop specified
                    //Using latest published package is inappropriate for release packages, because hotfix releases for old versions may be pushed after main major versions.
                    if (LatestByPublishDate && ResolverLooksForPreReleasePackage) {
                        latestPackage = packages.OrderByDescending(o => o.Published).FirstOrDefault();
                        if (latestPackage != null) { commandOutputProvider.Debug("'--latestbypublishdate' flag was specified. Package resolver will choose version of package by the latest publishing date instead of the higest SemVer version."); }
                    } else {
                        latestPackage = packages.FirstOrDefault();
                    }
                    



                    if (latestPackage == null && !string.IsNullOrWhiteSpace(versionPreReleaseTag) && !string.IsNullOrWhiteSpace(versionPreReleaseTagFallBacks)) {
                        commandOutputProvider.Debug("Could not find latest package with pre-release '{Tag:l}', falling back to search with pre-release tags '{FallBackTags:l}' ", versionPreReleaseTag, versionPreReleaseTagFallBacks);
                        //trim values and remove empty ones
                        List<string> versionPreReleaseTagFallBacksList = versionPreReleaseTagFallBacks.Split(',').ToList().Select(s => s.Trim()).ToList();
                        versionPreReleaseTagFallBacksList = versionPreReleaseTagFallBacksList.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

                        foreach (string versionPreReleaseTagFallBack in versionPreReleaseTagFallBacksList) {
                            //similar beahaviour as for general versionPreReleaseTag
                            //Get the latest published package for release instead of the package has the biggest SemVer
                            filters.Remove("take");

                            filters["preReleaseTag"] = versionPreReleaseTagFallBack;

                            ResolverLooksForPreReleasePackage = !(versionPreReleaseTagFallBack == "^$");

                            //As we can't sort by publishing date on the server side, we have to take all packages and sort them on the client side
                            if (LatestByPublishDate && ResolverLooksForPreReleasePackage)
                                filters["take"] = 10000;

                            packages = await repository.Client.Get<List<PackageResource>>(feed.Link("SearchTemplate"), filters).ConfigureAwait(false);

                            if (LatestByPublishDate && ResolverLooksForPreReleasePackage)
                            {
                                latestPackage = packages.OrderByDescending(o => o.Published).FirstOrDefault();
                                if (latestPackage != null) { commandOutputProvider.Debug("'--latestbypublishdate' flag was specified. Package resolver will choose version by the latest publishing date instead of the higest SemVer version."); }                                
                            }
                            else
                            {
                                latestPackage = packages.FirstOrDefault();
                            }
                            
                            if (latestPackage != null) {
                                versionSource = "versionPreReleaseTagFallBacks";
                                break; 
                            }
                        }
                        

                    }


                    if (latestPackage == null)
                    {
                        commandOutputProvider.Error("Could not find any packages with this ID in the feed '{FeedUri:l}'", feed.Name);
                    }
                    else
                    {
                        commandOutputProvider.Debug("Selected version '{Version:l}'", latestPackage.Version);
                        unresolved.SetVersionFromLatest(latestPackage.Version, versionSource);
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