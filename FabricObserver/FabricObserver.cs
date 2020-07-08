﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers;
using FabricObserver.Observers.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace FabricObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricObserver : StatelessService
    {
        private ObserverManager observerManager = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserver"/> class.
        /// </summary>
        /// <param name="context">StatelessServiceContext.</param>
        public FabricObserver(StatelessServiceContext context)
            : base(context)
        {
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return Array.Empty<ServiceInstanceListener>();
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        /// <returns>a Task.</returns>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!Debugger.IsAttached)
            {
                await Task.Delay(10);
            }

            ServiceCollection services = new ServiceCollection();

            this.ConfigureServices(services);

            using (ServiceProvider serviceProvider = services.BuildServiceProvider())
            {
                this.observerManager = new ObserverManager(serviceProvider, cancellationToken);
                await this.observerManager.StartObserversAsync().ConfigureAwait(false);
            }
        }

        private void ConfigureServices(ServiceCollection services)
        {
            services.AddScoped(typeof(IObserver), typeof(AppObserver));
            services.AddScoped(typeof(IObserver), typeof(CertificateObserver));
            services.AddScoped(typeof(IObserver), typeof(FabricSystemObserver));
            services.AddScoped(typeof(IObserver), typeof(NetworkObserver));
            services.AddScoped(typeof(IObserver), typeof(OsObserver));
            services.AddScoped(typeof(IObserver), typeof(SfConfigurationObserver));
            services.AddSingleton(typeof(System.Fabric.StatelessServiceContext), this.Context);

            this.LoadObserversFromPlugins(services);
        }

        private void LoadObserversFromPlugins(ServiceCollection services)
        {
            string pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");

            if (Directory.Exists(pluginsDir))
            {
                string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly);

                foreach (string pluginDll in pluginDlls)
                {
                    try
                    {
                        Assembly pluginAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginDll);
                        FabricObserverStartupAttribute[] startupAttributes =
                            pluginAssembly.GetCustomAttributes<FabricObserverStartupAttribute>().ToArray();

                        for (int i = 0; i < startupAttributes.Length; ++i)
                        {
                            object startupObject = System.Activator.CreateInstance(startupAttributes[i].StartupType);

                            if (startupObject is IFabricObserverStartup fabricObserverStartup)
                            {
                                fabricObserverStartup.ConfigureServices(services);
                            }
                        }
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }
        }
    }
}
