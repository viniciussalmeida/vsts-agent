using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Text;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    public interface IHandler : IAgentService
    {
        IExecutionContext ExecutionContext { get; set; }
        string FilePathInputRootDirectory { get; set; }
        Dictionary<string, string> Inputs { get; set; }
        string TaskDirectory { get; set; }

        Task RunAsync();
    }

    public abstract class Handler : AgentService
    {
        protected IWorkerCommandManager CommandManager { get; private set; }
        protected Dictionary<string, string> Environment { get; private set; }

        public IExecutionContext ExecutionContext { get; set; }
        public string FilePathInputRootDirectory { get; set; }
        public Dictionary<string, string> Inputs { get; set; }
        public string TaskDirectory { get; set; }

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            CommandManager = hostContext.GetService<IWorkerCommandManager>();
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        protected void AddEndpointsToEnvironment()
        {
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(ExecutionContext.Endpoints, nameof(ExecutionContext.Endpoints));

            int endpointsEnvBlockSize = 0;
            // Add the endpoints to the environment variable dictionary.
            foreach (ServiceEndpoint endpoint in ExecutionContext.Endpoints)
            {
                ArgUtil.NotNull(endpoint, nameof(endpoint));

                string partialKey = null;
                if (endpoint.Id != Guid.Empty)
                {
                    partialKey = endpoint.Id.ToString();
                }
                else if (string.Equals(endpoint.Name, ServiceEndpoints.SystemVssConnection, StringComparison.OrdinalIgnoreCase))
                {
                    partialKey = ServiceEndpoints.SystemVssConnection.ToUpperInvariant();
                }
                else if (endpoint.Data == null ||
                    !endpoint.Data.TryGetValue(WellKnownEndpointData.RepositoryId, out partialKey) ||
                    string.IsNullOrEmpty(partialKey))
                {
                    continue; // This should never happen.
                }

                endpointsEnvBlockSize += AddEnvironmentVariable(
                     key: $"ENDPOINT_URL_{partialKey}",
                     value: endpoint.Url?.ToString());
                endpointsEnvBlockSize += AddEnvironmentVariable(
                     key: $"ENDPOINT_AUTH_{partialKey}",
                    // Note, JsonUtility.ToString will not null ref if the auth object is null.
                    value: JsonUtility.ToString(endpoint.Authorization));
                if (endpoint.Authorization != null && endpoint.Authorization.Scheme != null)
                {
                    endpointsEnvBlockSize += AddEnvironmentVariable(
                        key: $"ENDPOINT_AUTH_SCHEME_{partialKey}",
                        value: endpoint.Authorization.Scheme);

                    foreach (KeyValuePair<string, string> pair in endpoint.Authorization.Parameters)
                    {
                        endpointsEnvBlockSize += AddEnvironmentVariable(
                            key: $"ENDPOINT_AUTH_PARAMETER_{partialKey}_{pair.Key?.Replace(' ', '_').ToUpperInvariant()}",
                            value: pair.Value);
                    }
                }
                if (endpoint.Id != Guid.Empty)
                {
                    endpointsEnvBlockSize += AddEnvironmentVariable(
                        key: $"ENDPOINT_DATA_{partialKey}",
                        // Note, JsonUtility.ToString will not null ref if the data object is null.
                        value: JsonUtility.ToString(endpoint.Data));

                    if (endpoint.Data != null)
                    {
                        foreach (KeyValuePair<string, string> pair in endpoint.Data)
                        {
                            endpointsEnvBlockSize += AddEnvironmentVariable(
                                key: $"ENDPOINT_DATA_{partialKey}_{pair.Key?.Replace(' ', '_').ToUpperInvariant()}",
                                value: pair.Value);
                        }
                    }
                }
            }

            if (endpointsEnvBlockSize > 0)
            {
                ExecutionContext.Output($"Endpoints has consumed {endpointsEnvBlockSize} bytes of environment blcok.");
            }
        }

        protected void AddSecureFilesToEnvironment()
        {
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));

            if (ExecutionContext.SecureFiles != null && ExecutionContext.SecureFiles.Count > 0)
            {
                int secureFilesEnvBlockSize = 0;
                // Add the secure files to the environment variable dictionary.
                foreach (SecureFile secureFile in ExecutionContext.SecureFiles)
                {
                    if (secureFile != null && secureFile.Id != Guid.Empty)
                    {
                        string partialKey = secureFile.Id.ToString();
                        secureFilesEnvBlockSize += AddEnvironmentVariable(
                            key: $"SECUREFILE_NAME_{partialKey}",
                            value: secureFile.Name
                        );
                        secureFilesEnvBlockSize += AddEnvironmentVariable(
                            key: $"SECUREFILE_TICKET_{partialKey}",
                            value: secureFile.Ticket
                        );
                    }
                }

                if (secureFilesEnvBlockSize > 0)
                {
                    ExecutionContext.Output($"SecureFiles has consumed {secureFilesEnvBlockSize} bytes of environment blcok.");
                }
            }
        }

        protected void AddInputsToEnvironment()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Inputs, nameof(Inputs));

            int inputsEnvBlockSize = 0;
            // Add the inputs to the environment variable dictionary.
            foreach (KeyValuePair<string, string> pair in Inputs)
            {
                inputsEnvBlockSize += AddEnvironmentVariable(
                    key: $"INPUT_{pair.Key?.Replace(' ', '_').ToUpperInvariant()}",
                    value: pair.Value);
            }

            if (inputsEnvBlockSize > 0)
            {
                ExecutionContext.Output($"Inputs has consumed {inputsEnvBlockSize} bytes of environment blcok.");
            }
        }

        protected void AddVariablesToEnvironment(bool excludeNames = false, bool excludeSecrets = false)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Environment, nameof(Environment));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(ExecutionContext.Variables, nameof(ExecutionContext.Variables));

            int variablesEnvBlockSize = 0;
            // Add the public variables.
            var names = new List<string>();
            foreach (KeyValuePair<string, string> pair in ExecutionContext.Variables.Public)
            {
                // Add "agent.jobstatus" using the unformatted name and formatted name.
                if (string.Equals(pair.Key, Constants.Variables.Agent.JobStatus, StringComparison.OrdinalIgnoreCase))
                {
                    variablesEnvBlockSize += AddEnvironmentVariable(pair.Key, pair.Value);
                }

                // Add the variable using the formatted name.
                string formattedKey = (pair.Key ?? string.Empty).Replace('.', '_').Replace(' ', '_').ToUpperInvariant();
                variablesEnvBlockSize += AddEnvironmentVariable(formattedKey, pair.Value);

                // Store the name.
                names.Add(pair.Key ?? string.Empty);
            }

            // Add the public variable names.
            if (!excludeNames)
            {
                variablesEnvBlockSize += AddEnvironmentVariable("VSTS_PUBLIC_VARIABLES", JsonUtility.ToString(names));
            }

            if (!excludeSecrets)
            {
                // Add the secret variables.
                var secretNames = new List<string>();
                foreach (KeyValuePair<string, string> pair in ExecutionContext.Variables.Private)
                {
                    // Add the variable using the formatted name.
                    string formattedKey = (pair.Key ?? string.Empty).Replace('.', '_').Replace(' ', '_').ToUpperInvariant();
                    variablesEnvBlockSize += AddEnvironmentVariable($"SECRET_{formattedKey}", pair.Value);

                    // Store the name.
                    secretNames.Add(pair.Key ?? string.Empty);
                }

                // Add the secret variable names.
                if (!excludeNames)
                {
                    variablesEnvBlockSize += AddEnvironmentVariable("VSTS_SECRET_VARIABLES", JsonUtility.ToString(secretNames));
                }
            }

            if (variablesEnvBlockSize > 0)
            {
                ExecutionContext.Output($"Variables has consumed {variablesEnvBlockSize} bytes of environment blcok.");
            }
        }

        protected int AddEnvironmentVariable(string key, string value)
        {
            ArgUtil.NotNullOrEmpty(key, nameof(key));
            Trace.Verbose($"Setting env '{key}' to '{value}'.");
            Environment[key] = value ?? string.Empty;

#if OS_WINDOWS
            return Encoding.Unicode.GetByteCount($"{key}={Environment[key]}\0");
#else
            // only windows has problem with Environment block size limit
            return 0;
#endif
        }

        protected void AddTaskVariablesToEnvironment()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext.TaskVariables, nameof(ExecutionContext.TaskVariables));

            int taskVariablesEnvBlockSize = 0;
            foreach (KeyValuePair<string, string> pair in ExecutionContext.TaskVariables.Public)
            {
                // Add the variable using the formatted name.
                string formattedKey = (pair.Key ?? string.Empty).Replace('.', '_').Replace(' ', '_').ToUpperInvariant();
                taskVariablesEnvBlockSize += AddEnvironmentVariable($"VSTS_TASKVARIABLE_{formattedKey}", pair.Value);
            }

            foreach (KeyValuePair<string, string> pair in ExecutionContext.TaskVariables.Private)
            {
                // Add the variable using the formatted name.
                string formattedKey = (pair.Key ?? string.Empty).Replace('.', '_').Replace(' ', '_').ToUpperInvariant();
                taskVariablesEnvBlockSize += AddEnvironmentVariable($"VSTS_TASKVARIABLE_{formattedKey}", pair.Value);
            }

            if (taskVariablesEnvBlockSize > 0)
            {
                ExecutionContext.Output($"TaskVariables has consumed {taskVariablesEnvBlockSize} bytes of environment blcok.");
            }
        }

        protected void AddPrependPathToEnvironment()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext.PrependPath, nameof(ExecutionContext.PrependPath));
            if (ExecutionContext.PrependPath.Count == 0)
            {
                return;
            }

            // prepend path section       
            int pathEnvBlockSize = 0;
            string prepend = string.Join(Path.PathSeparator.ToString(), ExecutionContext.PrependPath.Reverse<string>());
            string originalPath = ExecutionContext.Variables.Get(Constants.PathVariable) ?? System.Environment.GetEnvironmentVariable(Constants.PathVariable) ?? string.Empty;
            string newPath = VarUtil.PrependPath(prepend, originalPath);
            pathEnvBlockSize += AddEnvironmentVariable(Constants.PathVariable, newPath);

            if (pathEnvBlockSize > 0)
            {
                ExecutionContext.Output($"Prepend %Path% has consumed {pathEnvBlockSize} bytes of environment blcok.");
            }
        }
    }
}