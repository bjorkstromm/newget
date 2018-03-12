using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace NewGet
{
    public sealed class NewGetPackageInstaller
    {
        private const string ServiceIndex = "https://api.nuget.org/v3/index.json";
        private readonly HttpClient _httpClient;

        public NewGetPackageInstaller()
        {
            _httpClient = new HttpClient();
        }
        
        public async Task<IEnumerable<string>> InstallPackageAsync(string id, string version)
        {
            var serviceIndex = await GetServiceIndexAsync();

            var resource = serviceIndex.Resources.First(r =>
                r.Type.Equals("RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase));

            var semver = new NuGetVersion(version);
            var packageDependencies = new ConcurrentDictionary<PackageDependencyInfo, string>(PackageIdentityComparer.Default);
            var provider = DefaultFrameworkNameProvider.Instance;
            //var current = NuGetFramework.Parse(".NETFramework,Version=v4.7", provider);
            var current = NuGetFramework.Parse(".NETStandard,Version=v1.6", provider);
            await GetPackageDependencies(resource.Id, id, semver, current, packageDependencies);


            var target = packageDependencies.Keys.First(p => PackageIdentityComparer.Default.Equals(p, new PackageIdentity
            {
                Id = id,
                Version = semver
            }));
            var prunedPackages = PrunePackages(target, new HashSet<PackageDependencyInfo>(packageDependencies.Keys, PackageIdentityComparer.Default));

            return packageDependencies.Where(x => prunedPackages.Contains(x.Key)).Select(x => x.Value);
        }

        private static IEnumerable<PackageIdentity> PrunePackages(PackageDependencyInfo target, ISet<PackageDependencyInfo> availablePackages)
        {
            // Get all duplicate Ids. Sort by version and remove all but highest.
            var packagesToRemove = new HashSet<PackageIdentity>(availablePackages
                .GroupBy(x => x.Id)
                .Where(x => x.Count() > 1)
                .SelectMany(x => x.OrderByDescending(p => p.Version, VersionComparer.Default).Skip(1)),
                PackageIdentityComparer.Default);

            packagesToRemove.Add(new PackageDependencyInfo
            {
                Id = "NETStandard.Library",
                Version = new NuGetVersion(1,6,1)
            });

            var packagesSet = new HashSet<PackageIdentity>(PackageIdentityComparer.Default);
            PrunePackages(target, availablePackages, packagesToRemove, packagesSet);

            return packagesSet;
        }

        private static void PrunePackages(PackageDependencyInfo target,
            ISet<PackageDependencyInfo> availablePackages,
            ISet<PackageIdentity> packagesToRemove,
            ISet<PackageIdentity> result)
        {
            foreach (var dependency in target.Dependencies)
            {
                if (packagesToRemove.Contains(dependency))
                {
                    Console.WriteLine($"Skipping {dependency.Id}.{dependency.Version}. Marked for removal.");
                    continue;
                }

                if (result.Contains(dependency))
                {
                    Console.WriteLine($"Skipping {dependency.Id}.{dependency.Version}. Already added.");
                    continue;
                }

                PrunePackages(availablePackages.First(p => PackageIdentityComparer.Default.Equals(p, dependency)), availablePackages, packagesToRemove, result);
            }

            if (packagesToRemove.Contains(target) || result.Contains(target))
            {
                return;
            }
            result.Add(target);
            Console.WriteLine($"Adding {target.Id}.{target.Version}");
        }

        private async Task GetPackageDependencies(string registrationBaseUrl, string packageId, NuGetVersion version, NuGetFramework targetFramework, ConcurrentDictionary<PackageDependencyInfo, string> packages)
        {
            var package = await GetRegistrationLeaf(registrationBaseUrl, packageId, version);
            var packageIdentity = new PackageDependencyInfo
            {
                Id = package.CatalogEntry.PackageId,
                Version = package.CatalogEntry.Version
            };
            if (!packages.TryAdd(packageIdentity, package.PackageContent))
            {
                return;
            }
            Console.WriteLine($"Fetched {package.PackageContent}");

            try
            {
                if (package.CatalogEntry.DependencyGroups == null)
                {
                    return;
                }
                var reducer = new FrameworkReducer();
                var frameworks = package.CatalogEntry.DependencyGroups.Select(x => x.TargetFramework);
                var nearest = reducer.GetNearest(targetFramework, frameworks);
                var neareastDependencyGroup = package.CatalogEntry.DependencyGroups
                    .First(x => nearest.Equals(x.TargetFramework));

                if (neareastDependencyGroup.Dependencies != null)
                {
                    await Task.WhenAll(neareastDependencyGroup.Dependencies.Select(x =>
                    {
                        var dependency = new PackageIdentity
                        {
                            Id = x.Id,
                            Version = x.Range.MinVersion
                        };
                        packageIdentity.Dependencies.Add(dependency);
                        return GetPackageDependencies(registrationBaseUrl, dependency.Id, dependency.Version, targetFramework, packages);
                    }));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        private async Task<RegistrationLeaf> GetRegistrationLeaf(string registrationBaseUrl, string packageId, NuGetVersion version)
        {
            var registration = await GetRegistrationsAsync(registrationBaseUrl, packageId);

            return registration.Items
                .SelectMany(x => x.Items)
                .FirstOrDefault(x => x.CatalogEntry.Version.Equals(version, VersionComparison.Default));
        }

        private Task<RegistrationRoot> GetRegistrationsAsync(string registration)
        {
            return GetAsync<RegistrationRoot>(registration);
        }

        private Task<RegistrationRoot> GetRegistrationsAsync(string registrationBaseUrl, string id)
        {
            return GetRegistrationsAsync($"{registrationBaseUrl.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json");
        }

        private Task<ServiceIndex> GetServiceIndexAsync()
        {
            return GetAsync<ServiceIndex>(ServiceIndex);
        }

        private async Task<T> GetAsync<T>(string requestUri)
        {
            var response = await _httpClient.GetAsync(requestUri);

            if (!response.IsSuccessStatusCode)
            {
                return default(T);
            }

            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(content, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }
    }

    internal class PackageIdentity
    {
        public string Id { get; set; }
        public NuGetVersion Version { get; set; }
    }

    internal class PackageDependencyInfo : PackageIdentity
    {
        public List<PackageIdentity> Dependencies { get; set; } = new List<PackageIdentity>();
    }

    internal class PackageIdentityComparer : IEqualityComparer<PackageIdentity>
    {
        private readonly IVersionComparer _versionComparer;

        public PackageIdentityComparer()
        {
            _versionComparer = new VersionComparer(VersionComparison.Default);
        }

        public static PackageIdentityComparer Default => new PackageIdentityComparer();

        public bool Equals(PackageIdentity x, PackageIdentity y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (ReferenceEquals(x, null)
                || ReferenceEquals(y, null))
            {
                return false;
            }

            return _versionComparer.Equals(x.Version, y.Version)
                   && StringComparer.OrdinalIgnoreCase.Equals(x.Id, y.Id);
        }

        public int GetHashCode(PackageIdentity obj)
        {
            if (ReferenceEquals(obj, null))
            {
                return 0;
            }

            return obj.Id.GetHashCode() ^ obj.Version.GetHashCode();
        }
    }

    internal class RegistrationRoot
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("items")]
        public List<RegistrationPage> Items { get; set; }
    }

    internal class RegistrationPage
    {
        /// <summary>
        /// The URL to the registration page
        /// </summary>
        [JsonProperty("@id")]
        public string Id { get; set; }
        
        /// <summary>
        /// The number of registration leaves in the page
        /// </summary>
        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>
        /// The array of registration leaves and their associate metadata
        /// </summary>
        [JsonProperty("items")]
        public List<RegistrationLeaf> Items { get; set; }
        
        /// <summary>
        /// The lowest SemVer 2.0.0 version in the page (inclusive)
        /// </summary>
        [JsonProperty("lower")]
        [JsonConverter(typeof(NuGetVersionConverter))]
        public NuGetVersion Lower { get; set; }

        /// <summary>
        /// The URL to the registration index
        /// </summary>
        [JsonProperty("parent")]
        public string Parent { get; set; }

        /// <summary>
        /// The highest SemVer 2.0.0 version in the page (inclusive)
        /// </summary>
        [JsonProperty("upper")]
        [JsonConverter(typeof(NuGetVersionConverter))]
        public NuGetVersion Upper { get; set; }
    }

    internal class RegistrationLeaf
    {
        /// <summary>
        /// The URL to the registration leaf
        /// </summary>
        [JsonRequired]
        [JsonProperty("@id")]
        public string Id { get; set; }

        /// <summary>
        /// The catalog entry containing the package metadata
        /// </summary>
        [JsonRequired]
        [JsonProperty("catalogEntry")]
        public CatalogEntry CatalogEntry { get; set; }

        /// <summary>
        /// The URL to the package content (.nupkg)
        /// </summary>
        [JsonRequired]
        [JsonProperty("packageContent")]
        public string PackageContent { get; set; }
    }

    internal class CatalogEntry
    {
        /// <summary>
        /// The URL to document used to produce this object
        /// </summary>
        [JsonRequired]
        [JsonProperty("@id")]
        public string Id { get; set; }
        
//        [JsonProperty("authors")]
//        public List<string> Authors { get; set; }

        /// <summary>
        /// The URL to the package content (.nupkg)
        /// </summary>
        [JsonProperty("dependencyGroups")]
        public List<DependencyGroup> DependencyGroups { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("iconUrl")]
        public string IconUrl { get; set; }

        /// <summary>
        /// The ID of the package
        /// </summary>
        [JsonRequired]
        [JsonProperty("id")]
        public string PackageId { get; set; }

        [JsonProperty("licenseUrl")]
        public string LicenseUrl { get; set; }

        /// <summary>
        /// Should be considered as listed if absent
        /// </summary>
        [JsonProperty("listed")]
        public bool Listed { get; set; }

        [JsonProperty("minClientVersion")]
        [JsonConverter(typeof(NuGetVersionConverter))]
        public NuGetVersion MinClientVersion { get; set; }

        [JsonProperty("projectUrl")]
        public string ProjectUrl { get; set; }

        /// <summary>
        /// A string containing a ISO 8601 timestamp of when the package was published
        /// </summary>
        [JsonProperty("published")]
        public string Published { get; set; }

        [JsonProperty("requireLicenseAcceptance")]
        public bool RequireLicenseAcceptance { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }
        
        /// <summary>
        /// The version of the package
        /// </summary>
        /// <returns></returns>
        [JsonRequired]
        [JsonProperty("version")]
        [JsonConverter(typeof(NuGetVersionConverter))]
        public NuGetVersion Version { get; set; }
    }

    internal class DependencyGroup
    {

        /// <summary>
        /// The target framework that these dependencies are applicable to
        /// </summary>
        [JsonProperty("targetFramework")]
        [JsonConverter(typeof(NuGetFrameworkConverter))]
        public NuGetFramework TargetFramework { get; set; } = NuGetFramework.AnyFramework;

        
        [JsonProperty("dependencies")]
        public List<PackageDependency> Dependencies { get; set; }
    }

    internal class PackageDependency
    {
        /// <summary>
        /// The ID of the package dependency
        /// </summary>
        [JsonRequired]
        [JsonProperty("id")]
        public string Id { get; set; }
        
        /// <summary>
        /// The allowed version range of the dependency
        /// </summary>
        /// <returns></returns>
        [JsonProperty("range")]
        [JsonConverter(typeof(VersionRangeConverter))]
        public VersionRange Range { get; set; }
        
        /// <summary>
        /// The URL to the registration index for this dependency
        /// </summary>
        /// <returns></returns>
        [JsonProperty("registration")]
        public string Registration { get; set; }
    }

    internal class ServiceIndex
    {
        [JsonProperty("version")]
        [JsonConverter(typeof(NuGetVersionConverter))]
        public NuGetVersion Version { get; set; }

        [JsonProperty("resources")]
        public List<Resource> Resources { get; set; }
    }

    internal class Resource
    {
        [JsonProperty("@id")]
        public string Id { get; set; }
        
        [JsonProperty("@type")]
        public string Type { get; set; }
    }

    internal class NuGetVersionConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var value = reader.Value.ToString();
            return string.IsNullOrEmpty(value) ? null : NuGetVersion.Parse(value);
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string);
        }
    }

    internal class VersionRangeConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var value = reader.Value.ToString();
            var range = VersionRange.Parse(string.IsNullOrEmpty(value) ? "[0.0.0-alpha,)" : value);
            return new VersionRange(range.MinVersion, range.IsMinInclusive, range.MaxVersion, range.IsMaxInclusive);
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string);
        }
    }

    internal class NuGetFrameworkConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var value = reader.Value.ToString();
            return string.IsNullOrEmpty(value)
                ? NuGetFramework.AnyFramework
                : NuGetFramework.Parse(value);
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string);
        }
    }
}