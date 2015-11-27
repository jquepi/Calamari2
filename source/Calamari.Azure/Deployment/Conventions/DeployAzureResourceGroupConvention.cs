﻿using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Calamari.Commands.Support;
using Calamari.Deployment;
using Calamari.Deployment.Conventions;
using Calamari.Integration.FileSystem;
using Calamari.Util;
using Microsoft.Azure;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace Calamari.Azure.Deployment.Conventions
{
    public class DeployAzureResourceGroupConvention : IInstallConvention
    {
        readonly string templateFile;
        readonly string templateParametersFile;
        readonly ICalamariFileSystem fileSystem;

        public DeployAzureResourceGroupConvention(string templateFile, string templateParametersFile, ICalamariFileSystem fileSystem)
        {
            this.templateFile = templateFile;
            this.templateParametersFile = templateParametersFile;
            this.fileSystem = fileSystem;
        }

        public void Install(RunningDeployment deployment)
        {
            var variables = deployment.Variables;
            var subscriptionId = variables[SpecialVariables.Action.Azure.SubscriptionId];
            var tenantId = variables[SpecialVariables.Action.Azure.TenantId];
            var clientId = variables[SpecialVariables.Action.Azure.ClientId];
            var password = variables[SpecialVariables.Action.Azure.Password];
            var resourceGroupName = variables[SpecialVariables.Action.Azure.ResourceGroupName];
            var deploymentName = !string.IsNullOrWhiteSpace(variables[SpecialVariables.Action.Azure.ResourceGroupDeploymentName])
                    ? variables[SpecialVariables.Action.Azure.ResourceGroupDeploymentName]
                    : GenerateDeploymentNameFromStepName(variables[SpecialVariables.Action.Name]);
            var deploymentMode = (DeploymentMode) Enum.Parse(typeof (DeploymentMode),
                variables[SpecialVariables.Action.Azure.ResourceGroupDeploymentMode]);
            var template = variables.Evaluate(fileSystem.ReadFile(templateFile));
            var parameters = variables.Evaluate(fileSystem.ReadFile(templateParametersFile));

            Log.Info(
                $"Deploying Resource Group {resourceGroupName} in subscription {subscriptionId}.\nDeployment name: {deploymentName}\nDeployment mode: {deploymentMode}");

            Log.Verbose($"Template:\n{template}\n\nParameters:\n{parameters}");

            using (var armClient = new ResourceManagementClient(
                new TokenCloudCredentials(subscriptionId, GetAuthorizationToken(tenantId, clientId, password))))
            {
                CreateDeployment(armClient, resourceGroupName, deploymentName, deploymentMode, template, parameters);
                PollForCompletion(armClient, resourceGroupName, deploymentName);
            }
        }

        static string GetAuthorizationToken(string tenantId, string clientId, string password)
        {
            var context = new AuthenticationContext($"https://login.windows.net/{tenantId}");
            var result = context.AcquireToken("https://management.core.windows.net/",
                new ClientCredential(clientId, password));
            return result.AccessToken;
        }

        static string GenerateDeploymentNameFromStepName(string stepName)
        {
            var deploymentName = stepName ?? string.Empty;
            deploymentName = deploymentName.ToLower();
            deploymentName = Regex.Replace(deploymentName, "\\s", "-");
            deploymentName =
                new string(deploymentName.Select(x => (char.IsLetterOrDigit(x) || x == '-') ? x : '-').ToArray());
            deploymentName = Regex.Replace(deploymentName, "-+", "-");
            deploymentName = deploymentName.Trim('-', '/');
            deploymentName = deploymentName + "-" + AesEncryption.RandomString(6);
            return deploymentName;
        }

        static void CreateDeployment(ResourceManagementClient armClient, string resourceGroupName, string deploymentName,
            DeploymentMode deploymentMode, string template, string parameters)
        {
            var createDeploymentResult = armClient.Deployments.CreateOrUpdate(resourceGroupName, deploymentName,
                new Microsoft.Azure.Management.Resources.Models.Deployment
                {
                    Properties = new DeploymentProperties
                    {
                        Mode = deploymentMode,
                        Template = template,
                        Parameters = parameters
                    }
                });

            Log.Info($"Deployment created: {createDeploymentResult.Deployment.Id}");
        }

        static void PollForCompletion(IResourceManagementClient armClient, string resourceGroupName,
            string deploymentName)
        {
            var currentPollWait = 1;
            var previousPollWait = 0;
            var continueToPoll = true;

            while (continueToPoll)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(currentPollWait, 30)));

                Log.Verbose("Polling for status of deployment...");
                var deployment = armClient.Deployments.Get(resourceGroupName, deploymentName).Deployment;
                Log.Verbose($"Provisioning state: {deployment.Properties.ProvisioningState}");

                switch (deployment.Properties.ProvisioningState)
                {
                    case "Succeeded":
                        Log.Info($"Deployment {deploymentName} complete.");  
                        Log.Info("Deployment Outputs:");
                        Log.Info(!string.IsNullOrWhiteSpace(deployment.Properties.Outputs) ? deployment.Properties.Outputs : "No deployment outputs");
                        Log.Info(GetOperationResults(armClient, resourceGroupName, deploymentName));
                        continueToPoll = false;
                        break;

                    case "Failed":
                        throw new CommandException($"Azure Resource Group deployment {deploymentName} failed:\n" + GetOperationResults(armClient, resourceGroupName, deploymentName));

                    default:
                        var temp = previousPollWait;
                        previousPollWait = currentPollWait;
                        currentPollWait = temp + currentPollWait;
                        break;
                }
            }
        }

        static string GetOperationResults(IResourceManagementClient armClient, string resourceGroupName, string deploymentName)
        {
            var log = new StringBuilder("Operations details:\n");
            var operations =
                armClient.DeploymentOperations.List(resourceGroupName, deploymentName,
                    new DeploymentOperationsListParameters()).Operations;

            foreach (var operation in operations)
            {
                log.AppendLine($"Resource: {operation.Properties.TargetResource.ResourceName}");
                log.AppendLine($"Type: {operation.Properties.TargetResource.ResourceType}");
                log.AppendLine($"Timestamp: {operation.Properties.Timestamp.ToLocalTime().ToString("s")}");
                log.AppendLine($"Deployment operation: {operation.Id}");
                log.AppendLine($"Status: {operation.Properties.StatusCode}");
                log.AppendLine($"Provisioning State: {operation.Properties.ProvisioningState}");
                if (!string.IsNullOrWhiteSpace(operation.Properties.StatusMessage))
                {
                    log.AppendLine($"Status Message: {operation.Properties.StatusMessage}");
                }
            }

            return log.ToString();
        }
    }
}