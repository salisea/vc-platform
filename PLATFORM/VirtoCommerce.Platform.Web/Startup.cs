﻿using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using Microsoft.Owin;
using Microsoft.Owin.StaticFiles;
using Microsoft.Practices.Unity;
using NuGet;
using Owin;
using VirtoCommerce.Platform.Core.Asset;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Web;
using WebGrease.Extensions;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Data.Asset;
using VirtoCommerce.Platform.Data.Packaging;
using VirtoCommerce.Platform.Data.Packaging.Repositories;
using VirtoCommerce.Platform.Web.Controllers.Api;

[assembly: OwinStartup(typeof(Startup))]

namespace VirtoCommerce.Platform.Web
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            const string modulesVirtualPath = "~/Modules";
            var modulesPhysicalPath = HostingEnvironment.MapPath(modulesVirtualPath).EnsureEndSeparator();
            var assembliesPath = HostingEnvironment.MapPath("~/App_data/Modules");

            var bootstraper = new VirtoCommercePlatformWebBootstraper(modulesVirtualPath, modulesPhysicalPath, assembliesPath);
            bootstraper.Run();
            bootstraper.Container.RegisterInstance(app);

            var moduleCatalog = bootstraper.Container.Resolve<IModuleCatalog>();

            // Register URL rewriter before modules initialization
            if (Directory.Exists(modulesPhysicalPath))
            {
                var applicationBase = AppDomain.CurrentDomain.SetupInformation.ApplicationBase.EnsureEndSeparator();
                var modulesRelativePath = MakeRelativePath(applicationBase, modulesPhysicalPath);

                var urlRewriterOptions = new UrlRewriterOptions();
                var moduleInitializerOptions = (ModuleInitializerOptions)bootstraper.Container.Resolve<IModuleInitializerOptions>();
                moduleInitializerOptions.SampleDataLevel = EnumUtility.SafeParse(ConfigurationManager.AppSettings["VirtoCommerce:SampleDataLevel"], SampleDataLevel.None);

                foreach (var module in moduleCatalog.Modules.OfType<ManifestModuleInfo>())
                {
                    var urlRewriteKey = string.Format(CultureInfo.InvariantCulture, "/Modules/$({0})", module.ModuleName);
                    var urlRewriteValue = MakeRelativePath(modulesPhysicalPath, module.FullPhysicalPath);
                    urlRewriterOptions.Items.Add(PathString.FromUriComponent(urlRewriteKey), "/" + urlRewriteValue);

                    moduleInitializerOptions.ModuleDirectories.Add(module.ModuleName, module.FullPhysicalPath);
                }

                app.Use<UrlRewriterOwinMiddleware>(urlRewriterOptions);
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileSystem = new Microsoft.Owin.FileSystems.PhysicalFileSystem(modulesRelativePath)
                });
            }

            //Initialize Platform dependencies
            PlatformInitialize(bootstraper.Container);

            // Ensure all modules are loaded
            var moduleManager = bootstraper.Container.Resolve<IModuleManager>();

            foreach (var module in moduleCatalog.Modules.Where(x => x.State == ModuleState.NotStarted))
            {
                moduleManager.LoadModule(module.ModuleName);
            }

            // Post-initialize
            var postInitializeModules = moduleCatalog.CompleteListWithDependencies(moduleCatalog.Modules)
                .Select(m => m.ModuleInstance)
                .OfType<IPostInitialize>()
                .ToArray();

            foreach (var module in postInitializeModules)
            {
                module.PostInitialize();
            }
        }


        private void PlatformInitialize(IUnityContainer container)
        {
            #region Assets

            var assetsConnection = ConfigurationManager.ConnectionStrings["AssetsConnectionString"];

            if (assetsConnection != null)
            {
                var properties = assetsConnection.ConnectionString.ToDictionary(";", "=");
                var provider = properties["provider"];

                if (string.Equals(provider, FileSystemBlobProvider.ProviderName))
                {
                    var rootPath = HostingEnvironment.MapPath(properties["rootPath"]);
                    var publicUrl = properties["publicUrl"];
                    var fileSystemBlobProvider = new FileSystemBlobProvider(rootPath, publicUrl);
                    container.RegisterInstance<IBlobStorageProvider>(fileSystemBlobProvider);
                    container.RegisterInstance<IBlobUrlResolver>(fileSystemBlobProvider);
                }
            }

            #endregion

            #region Packaging

            var sourcePath = HostingEnvironment.MapPath("~/App_Data/SourcePackages");
            var packagesPath = HostingEnvironment.MapPath("~/App_Data/InstalledPackages");

            var manifestProvider = container.Resolve<IModuleManifestProvider>();
            var modulesPath = manifestProvider.RootPath;

            var projectSystem = new WebsiteProjectSystem(modulesPath);

            var nugetProjectManager = new ProjectManager(
                new WebsiteLocalPackageRepository(sourcePath),
                new DefaultPackagePathResolver(modulesPath),
                projectSystem,
                new ManifestPackageRepository(manifestProvider, new WebsitePackageRepository(packagesPath, projectSystem))
            );

            var packageService = new PackageService(nugetProjectManager);

            container.RegisterType<ModulesController>(new InjectionConstructor(packageService, sourcePath));

            #endregion
        }

        private static string MakeRelativePath(string rootPath, string fullPath)
        {
            var rootUri = new Uri(rootPath);
            var fullUri = new Uri(fullPath);
            var relativePath = rootUri.MakeRelativeUri(fullUri).ToString();
            return relativePath;
        }
    }
}
