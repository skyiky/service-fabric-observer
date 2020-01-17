﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Utilities.Telemetry;
using FabricClusterObserver.Utilities;

namespace FabricClusterObserver
{
    class ClusterObserver : ObserverBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer runs on one node and as an independent service since FabricObserver 
        /// is a -1 singleton partition service (runs on every node). ClusterObserver and FabricObserver
        /// can run in the same cluster as they are independent processes.
        /// </summary>
        public ClusterObserver()
            : base(ObserverConstants.ClusterObserverName)
        {
        }

        private HealthState ClusterHealthState { get; set; } = HealthState.Unknown;

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                this.Token.ThrowIfCancellationRequested();

                return;
            }

            await ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            await this.ProbeClusterHealthAsync(token).ConfigureAwait(true);
        }

        private async Task ProbeClusterHealthAsync(CancellationToken token)
        {
            if (!this.IsTelemetryEnabled)
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            _ = bool.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitHealthWarningEvaluationConfigurationSetting), out bool emitWarningDetails);
            
            _ = bool.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitOkHealthState), out bool emitOkHealthState);

            try
            {
                var clusterHealth = await this.FabricClientInstance.HealthManager.GetClusterHealthAsync(
                                                this.AsyncClusterOperationTimeoutSeconds,
                                                token).ConfigureAwait(true);

                string telemetryDescription = string.Empty;

                // Previous run generated unhealthy evaluation report. Clear it (send Ok) .
                if (emitOkHealthState && clusterHealth.AggregatedHealthState == HealthState.Ok
                    && (this.ClusterHealthState == HealthState.Error
                    || (emitWarningDetails && this.ClusterHealthState == HealthState.Warning)))
                {
                    telemetryDescription += "Cluster has recovered from previous Error/Warning state.";
                }
                else // Construct unhealthy state information.
                {
                    // If in Warning and you are not sending Warning state reports, then end here.
                    if (!emitWarningDetails && clusterHealth.AggregatedHealthState == HealthState.Warning)
                    {
                        return;
                    }

                    var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                    foreach (var evaluation in unhealthyEvaluations)
                    {
                        token.ThrowIfCancellationRequested();

                        telemetryDescription += $"{Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind)} - {evaluation.AggregatedHealthState}: {evaluation.Description}{Environment.NewLine}";

                        // Application in error/warning?.
                        foreach (var app in clusterHealth.ApplicationHealthStates)
                        {
                            if (app.AggregatedHealthState == HealthState.Ok
                                || (emitWarningDetails && app.AggregatedHealthState != HealthState.Warning))
                            {
                                continue;
                            }

                            telemetryDescription += $"Application in Error or Warning: {app.ApplicationName}{Environment.NewLine}";
                        }
                    }
                }

                // Track current health state for use in next run.
                this.ClusterHealthState = clusterHealth.AggregatedHealthState;

                // This means there is no cluster health state data to emit.
                if (string.IsNullOrEmpty(telemetryDescription))
                {
                    return;
                }

                // Telemetry.
                await this.ObserverTelemetryClient?.ReportHealthAsync(
                        HealthScope.Cluster,
                        "AggregatedClusterHealth",
                        clusterHealth.AggregatedHealthState,
                        telemetryDescription,
                        this.ObserverName,
                        this.Token);
            }
            catch (ArgumentException ae) 
            { 
                this.ObserverLogger.LogError(
                    "Unable to determine cluster health:{0}{1}",
                    Environment.NewLine,
                    ae.ToString()); 
            }
            catch (FabricException fe) 
            { 
                this.ObserverLogger.LogError(
                    "Unable to determine cluster health:{0}{1}",
                    Environment.NewLine,
                    fe.ToString()); 
            }
            catch (TimeoutException te) 
            { 
                this.ObserverLogger.LogError(
                    "Unable to determine cluster health:{0}{1}",
                    Environment.NewLine,
                    te.ToString()); 
            }
            catch (Exception e)
            {
                this.ObserverLogger.LogError(
                    "Unable to determine cluster health:{0}{1}",
                    Environment.NewLine,
                    e.ToString());

                throw;
            }
        }
    }
}
