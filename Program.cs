using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using System.ClientModel;

namespace TaxIQExtraction
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Load prompt from file
            var promptPath = Path.Combine(AppContext.BaseDirectory, "ExtractionPrompt.txt");
            if (!File.Exists(promptPath))
            {
                Console.WriteLine($"Error: Prompt file not found at {promptPath}");
                return;
            }
            var prompt = await File.ReadAllTextAsync(promptPath);
 
            // Load configuration from appsettings.json
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Read Azure OpenAI settings from configuration
            var endpoint = configuration["AzureOpenAI:Endpoint"]
                ?? throw new InvalidOperationException("Azure OpenAI Endpoint not configured in appsettings.json");
            var apiKey = configuration["AzureOpenAI:ApiKey"]
                ?? throw new InvalidOperationException("Azure OpenAI ApiKey not configured in appsettings.json");
            var deploymentName = configuration["AzureOpenAI:DeploymentName"]
                ?? throw new InvalidOperationException("Azure OpenAI DeploymentName not configured in appsettings.json");

            Console.WriteLine("=== Azure OpenAI Optimized Throughput Load Test ===\n");

            // Ask for which test option that I want to run
            Console.WriteLine("Select Test Option:");
            Console.WriteLine("  1. Run all tests at once");
            Console.WriteLine("  2. Run Fixed Batch Size Load Test");
            Console.WriteLine("  3. Optimized Throughput Load Test");
            Console.WriteLine("  4. Dynamic Batching Load Test");
            Console.Write("Enter option number (default 1): ");

            var optionInput = Console.ReadLine();

            // Default to option 1 if no input
            optionInput = string.IsNullOrWhiteSpace(optionInput) ? "1" : optionInput;
            await (optionInput switch
            {
                "1" => RunStraightLoadTest(prompt, endpoint, apiKey, deploymentName),

                // Add other cases as needed

                "2" => RunFixedBatchSizeLoadTest(prompt, endpoint, apiKey, deploymentName),
                "3" => RunOptimizedThroughputLoadTest(prompt, endpoint, apiKey, deploymentName),
                "4" => RunDynamicBatchingLoadTest(prompt, endpoint, apiKey, deploymentName),
                _ => RunStraightLoadTest(prompt, endpoint, apiKey, deploymentName),
            });
        }

        static async Task RunStraightLoadTest(string prompt, string endpoint, string apiKey, string deploymentName)
        {
            Console.WriteLine("Straight Load Test...\n");
            Console.WriteLine("---------------------------\n");

            Console.WriteLine("\nTest Configuration:");
            Console.Write("  Total number of requests? (default 1000): ");
            var totalInput = Console.ReadLine();
            int totalRequests = string.IsNullOrWhiteSpace(totalInput) ? 1000 : int.Parse(totalInput);

            AzureOpenAIClientOptions clientOptions = new();
            clientOptions.NetworkTimeout = TimeSpan.FromMinutes(5); // To prevent timeouts when obtaining long responses

            AzureOpenAIClient azureClient = new(
                new Uri(endpoint),
                new ApiKeyCredential(apiKey),
                clientOptions
            );

            ChatClient chatClient = azureClient.GetChatClient(deploymentName);

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 16000,
                Temperature = 1,
                TopP = 1,
                FrequencyPenalty = 0,
                PresencePenalty = 0
            };

            Console.WriteLine("Starting execution...");
            Console.WriteLine("Press Ctrl+C to cancel\n");

            var startTime = DateTime.UtcNow;

            // Send all requests at once
            var tasks = new List<Task<RequestResult>>();
            for (int i = 0; i < totalRequests; i++)
            {
                int requestId = i + 1;
                tasks.Add(SendRequestAsync(chatClient, prompt, options, requestId));
            }

            Console.WriteLine($"{totalRequests} requests sent. Waiting for responses...\n");

            // Wait for all requests to complete
            var results = await Task.WhenAll(tasks);

            var endTime = DateTime.UtcNow;
            var totalDuration = endTime - startTime;
            
            // Calculate statistics
            var successful = results.Where(r => r.Success).ToArray();
            var failed = results.Where(r => !r.Success).ToArray();
            Console.WriteLine($"\nComplete - Success: {successful.Length}, Failed: {failed.Length}");

            Console.WriteLine("\n=== FINAL RESULTS ===");
            Console.WriteLine($"Total Requests: {totalRequests}");
            Console.WriteLine($"Successful: {successful.Length}");
            Console.WriteLine($"Failed: {failed.Length}");
            Console.WriteLine($"Total Duration: {totalDuration.TotalSeconds:F2} seconds");

            if (successful.Any())
            {
                var avgDuration = successful.Average(r => r.Duration.TotalSeconds);
                var minDuration = successful.Min(r => r.Duration.TotalSeconds);
                var maxDuration = successful.Max(r => r.Duration.TotalSeconds);
                var totalInputTokens = successful.Sum(r => r.InputTokens);
                var totalOutputTokens = successful.Sum(r => r.OutputTokens);
                var totalTokens = successful.Sum(r => r.TotalTokens);
                var requestsPerSecond = totalRequests / totalDuration.TotalSeconds;

                Console.WriteLine($"\nPerformance Metrics:");
                Console.WriteLine($"  Requests/Second: {requestsPerSecond:F2}");
                Console.WriteLine($"  Avg Response Time: {avgDuration:F2}s");
                Console.WriteLine($"  Min Response Time: {minDuration:F2}s");
                Console.WriteLine($"  Max Response Time: {maxDuration:F2}s");

                Console.WriteLine($"\nToken Usage:");
                Console.WriteLine($"  Total Input Tokens: {totalInputTokens:N0}");
                Console.WriteLine($"  Total Output Tokens: {totalOutputTokens:N0}");
                Console.WriteLine($"  Total Tokens: {totalTokens:N0}");
                Console.WriteLine($"  Avg Tokens/Request: {(totalTokens / successful.Length):F0}");
                Console.WriteLine($"  Actual Tokens/Min: {(totalTokens / (totalDuration.TotalMinutes)):F0}");
                Console.WriteLine($"  Actual Requests/Min: {(totalRequests / (totalDuration.TotalMinutes)):F1}");
            }

            if (failed.Any())
            {
                Console.WriteLine($"\n=== FAILED REQUESTS ({failed.Length}) ===");
                foreach (var failure in failed.Take(10))
                {
                    Console.WriteLine($"Request #{failure.RequestId}: {failure.ErrorMessage}");
                }
                if (failed.Length > 10)
                {
                    Console.WriteLine($"... and {failed.Length - 10} more failures");
                }
            }

            // Only offer to display individual results when the total number of requests is less than 5
            if (totalRequests < 5)
            {
                // Display individual request results
                Console.WriteLine("\n=== REQUEST RESULTS ===");
                Console.Write("Display all results? (y/N): ");
                var displayAll = Console.ReadLine();

                if (!string.IsNullOrWhiteSpace(displayAll) && displayAll.ToLower() == "y")
                {
                    foreach (var result in results.OrderBy(r => r.RequestId))
                    {
                        Console.WriteLine($"\n--- Request #{result.RequestId} ---");
                        Console.WriteLine($"Status: {(result.Success ? "✓ Success" : "✗ Failed")}");
                        Console.WriteLine($"Duration: {result.Duration.TotalSeconds:F2}s");

                        if (result.Success)
                        {
                            Console.WriteLine($"Tokens: Input={result.InputTokens}, Output={result.OutputTokens}, Total={result.TotalTokens}");
                            Console.WriteLine($"Response:\n{result.Response}");
                        }
                        else
                        {
                            Console.WriteLine($"Error: {result.ErrorMessage}");
                        }
                    }
                }
                else
                {
                    // Show just a sample
                    var sample = successful.FirstOrDefault();
                    if (sample != null)
                    {
                        Console.WriteLine($"\n--- Sample Request #{sample.RequestId} ---");
                        Console.WriteLine($"Duration: {sample.Duration.TotalSeconds:F2}s");
                        Console.WriteLine($"Tokens: Input={sample.InputTokens}, Output={sample.OutputTokens}, Total={sample.TotalTokens}");
                        Console.WriteLine($"Response:\n{sample.Response}");
                    }
                }
            }

            // write start and end time to console
            Console.WriteLine($"\nTest started at: {startTime.ToLocalTime()}");
            Console.WriteLine($"Test ended at: {endTime.ToLocalTime()}");
        }

        static async Task RunFixedBatchSizeLoadTest(string prompt, string endpoint, string apiKey, string deploymentName)
        {
            try
            {
                // Ask for Configuration inputs
                Console.WriteLine("\nTest Configuration:");
                Console.Write("  Total number of requests? (default 1000): ");
                var totalInput = Console.ReadLine();
                int totalRequests = string.IsNullOrWhiteSpace(totalInput) ? 1000 : int.Parse(totalInput);

                Console.Write("  Requests per batch? (default 40): ");
                var batchSizeInput = Console.ReadLine();
                int batchSize = string.IsNullOrWhiteSpace(batchSizeInput) ? 40 : int.Parse(batchSizeInput);

                Console.Write("  Delay between batches in seconds? (default 1): ");
                var delayInput = Console.ReadLine();
                int delaySeconds = string.IsNullOrWhiteSpace(delayInput) ? 1 : int.Parse(delayInput);

                int numberOfBatches = (int)Math.Ceiling((double)totalRequests / batchSize);

                AzureOpenAIClientOptions clientOptions = new();
                clientOptions.NetworkTimeout = TimeSpan.FromMinutes(5); // To prevent timeouts when obtaining long responses

                AzureOpenAIClient azureClient = new(
                    new Uri(endpoint),
                    new ApiKeyCredential(apiKey),
                    clientOptions
                );

                ChatClient chatClient = azureClient.GetChatClient(deploymentName);

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 16000,
                    Temperature = 1,
                    TopP = 1,
                    FrequencyPenalty = 0,
                    PresencePenalty = 0
                };

                Console.WriteLine("Starting batched execution...");
                Console.WriteLine("Press Ctrl+C to cancel\n");

                var startTime = DateTime.UtcNow;


                // Execute requests in batches
                var allResults = new List<RequestResult>();
                int requestCounter = 0;

                for (int batchNum = 0; batchNum < numberOfBatches; batchNum++)
                {
                    int requestsInThisBatch = Math.Min(batchSize, totalRequests - requestCounter);

                    Console.WriteLine($"\n=== Batch {batchNum + 1}/{numberOfBatches} ({requestsInThisBatch} requests) ===");

                    var batchTasks = new List<Task<RequestResult>>();

                    for (int i = 0; i < requestsInThisBatch; i++)
                    {
                        int requestId = requestCounter + i + 1;
                        batchTasks.Add(SendRequestAsync(chatClient, prompt, options, requestId));
                    }

                    // Wait for all tasks in this batch to complete
                    var batchResults = await Task.WhenAll(batchTasks);
                    allResults.AddRange(batchResults);

                    requestCounter += requestsInThisBatch;

                    var batchSuccessful = batchResults.Count(r => r.Success);
                    var batchFailed = batchResults.Count(r => !r.Success);
                    Console.WriteLine($"\nBatch {batchNum + 1} Complete - Success: {batchSuccessful}, Failed: {batchFailed}");

                    // Add delay between batches (except after the last batch)
                    if (batchNum < numberOfBatches - 1 && delaySeconds > 0)
                    {
                        Console.WriteLine($"Waiting {delaySeconds} seconds before next batch...");
                        await Task.Delay(delaySeconds * 1000);
                    }
                }

                var endTime = DateTime.UtcNow;
                var totalDuration = endTime - startTime;
                var results = allResults.ToArray();

                // Calculate statistics
                var successful = results.Where(r => r.Success).ToArray();
                var failed = results.Where(r => !r.Success).ToArray();

                Console.WriteLine("\n=== FINAL RESULTS ===");
                Console.WriteLine($"Total Requests: {totalRequests}");
                Console.WriteLine($"Successful: {successful.Length}");
                Console.WriteLine($"Failed: {failed.Length}");
                Console.WriteLine($"Total Duration: {totalDuration.TotalSeconds:F2} seconds");
                Console.WriteLine($"Number of Batches: {numberOfBatches}");
                Console.WriteLine($"Batch Size: {batchSize}");

                if (successful.Any())
                {
                    var avgDuration = successful.Average(r => r.Duration.TotalSeconds);
                    var minDuration = successful.Min(r => r.Duration.TotalSeconds);
                    var maxDuration = successful.Max(r => r.Duration.TotalSeconds);
                    var totalInputTokens = successful.Sum(r => r.InputTokens);
                    var totalOutputTokens = successful.Sum(r => r.OutputTokens);
                    var totalTokens = successful.Sum(r => r.TotalTokens);
                    var requestsPerSecond = totalRequests / totalDuration.TotalSeconds;

                    Console.WriteLine($"\nPerformance Metrics:");
                    Console.WriteLine($"  Requests/Second: {requestsPerSecond:F2}");
                    Console.WriteLine($"  Avg Response Time: {avgDuration:F2}s");
                    Console.WriteLine($"  Min Response Time: {minDuration:F2}s");
                    Console.WriteLine($"  Max Response Time: {maxDuration:F2}s");

                    Console.WriteLine($"\nToken Usage:");
                    Console.WriteLine($"  Total Input Tokens: {totalInputTokens:N0}");
                    Console.WriteLine($"  Total Output Tokens: {totalOutputTokens:N0}");
                    Console.WriteLine($"  Total Tokens: {totalTokens:N0}");
                    Console.WriteLine($"  Avg Tokens/Request: {(totalTokens / successful.Length):F0}");
                    Console.WriteLine($"  Actual Tokens/Min: {(totalTokens / (totalDuration.TotalMinutes)):F0}");
                    Console.WriteLine($"  Actual Requests/Min: {(totalRequests / (totalDuration.TotalMinutes)):F1}");
                }

                if (failed.Any())
                {
                    Console.WriteLine($"\n=== FAILED REQUESTS ({failed.Length}) ===");
                    foreach (var failure in failed.Take(10))
                    {
                        Console.WriteLine($"Request #{failure.RequestId}: {failure.ErrorMessage}");
                    }
                    if (failed.Length > 10)
                    {
                        Console.WriteLine($"... and {failed.Length - 10} more failures");
                    }
                }

                // write start and end time to console
                Console.WriteLine($"\nTest started at: {startTime.ToLocalTime()}");
                Console.WriteLine($"Test ended at: {endTime.ToLocalTime()}");
               
                //Console.WriteLine("\nPress any key to exit...");
                //Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

        }

        static async Task RunDynamicBatchingLoadTest(string prompt, string endpoint, string apiKey, string deploymentName)
        {
            try
            {
                Console.WriteLine("\nTest Configuration:");
                Console.Write("  Total number of requests? (default 1000): ");
                var totalInput = Console.ReadLine();
                int totalRequests = string.IsNullOrWhiteSpace(totalInput) ? 1000 : int.Parse(totalInput);

                AzureOpenAIClientOptions clientOptions = new();
                clientOptions.NetworkTimeout = TimeSpan.FromMinutes(5); // To prevent timeouts when obtaining long responses

                AzureOpenAIClient azureClient = new(
                    new Uri(endpoint),
                    new ApiKeyCredential(apiKey),
                    clientOptions
                );

                ChatClient chatClient = azureClient.GetChatClient(deploymentName);

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 16000,
                    Temperature = 1,
                    TopP = 1,
                    FrequencyPenalty = 0,
                    PresencePenalty = 0
                };

                Console.WriteLine("Starting execution...");
                Console.WriteLine("Press Ctrl+C to cancel\n");

                var startTime = DateTime.UtcNow;
                
                // Execute requests in batches
                var allResults = new List<RequestResult>();
                int requestCounter = 0;

                int batchSize = 10;
                int numberOfBatches = (int)Math.Ceiling((double)totalRequests / batchSize);
                int delaySeconds = 1;

                for (int batchNum = 0; batchNum < numberOfBatches; batchNum++)
                {
                    int requestsInThisBatch = Math.Min(batchSize, totalRequests - requestCounter);
                    var batchTasks = new List<Task<RequestResult>>();
                    //Console.WriteLine($"\n=== Batch {batchNum + 1}/{numberOfBatches} ({requestsInThisBatch} requests) ===");
                    var batchStartTime = DateTime.UtcNow;

                    for (int i = 0; i < requestsInThisBatch; i++)
                    {
                        int requestId = requestCounter + i + 1;
                        // Console.WriteLine($"Scheduling Request {requestId}");
                        batchTasks.Add(SendRequestAsync(chatClient, prompt, options, requestId));
                    }

                    // Wait for all tasks in this batch to complete
                    var batchResults = await Task.WhenAll(batchTasks);
                    allResults.AddRange(batchResults);

                    requestCounter += requestsInThisBatch;

                    // Get duration of the batch
                    var batchDurationMax = batchResults.Max(r => r.Duration.TotalSeconds);
                    Console.WriteLine($"\nMax batch duration: {batchDurationMax:F2} seconds\nBatch started: {batchStartTime.ToLocalTime()}   Batch ended: {DateTime.UtcNow.ToLocalTime()} ");

                    // get success count of the batch
                    var batchSuccess = batchResults[0].Success;

                    var batchSuccessful = batchResults.Count(r => r.Success);
                    var batchFailed = batchResults.Count(r => !r.Success);
                    Console.WriteLine($"Batch {batchNum + 1} Complete - Success: {batchSuccessful}, Failed: {batchFailed}");

                    var batchEndTime = DateTime.UtcNow;
                    var batchDuration = batchEndTime - batchStartTime;
                    Console.WriteLine($"Batch took: {batchDuration.TotalSeconds:F2} seconds");

                    // Add delay between batches (except after the last batch)
                    if (batchDurationMax > 60 && batchSuccess)
                    {
                        Console.WriteLine($"Max request took {batchDurationMax:F2} seconds, adding delay of {delaySeconds} seconds before next request...");
                        await Task.Delay(delaySeconds * 1000);
                    }
                    else if (batchDurationMax > 30 && batchSuccess)
                    {
                        int delaySeconds2 = 60 - (int)batchDurationMax;
                        Console.WriteLine($"Max request took {batchDurationMax:F2} seconds, adding delay of {delaySeconds2} seconds before next request...");
                        await Task.Delay(delaySeconds2 * 1000);
                    }
                    else if (!batchSuccess)
                    {
                        int delaySeconds2 = 60;
                        Console.WriteLine($"Batch failed, adding delay of {delaySeconds2} seconds before next request...");
                        await Task.Delay(delaySeconds2 * 1000);
                    }
                    else
                    {
                        Console.WriteLine($"Waiting {delaySeconds} seconds before next batch...");
                        await Task.Delay(delaySeconds * 1000);
                    }
                }

                var endTime = DateTime.UtcNow;
                var totalDuration = endTime - startTime;
                var results = allResults.ToArray();

                // Calculate statistics
                var successful = results.Where(r => r.Success).ToArray();
                var failed = results.Where(r => !r.Success).ToArray();

                Console.WriteLine("\n=== FINAL RESULTS ===");
                Console.WriteLine($"Total Requests: {totalRequests}");
                Console.WriteLine($"Successful: {successful.Length}");
                Console.WriteLine($"Failed: {failed.Length}");
                Console.WriteLine($"Total Duration: {totalDuration.TotalSeconds:F2} seconds");
                
                if (successful.Any())
                {
                    var avgDuration = successful.Average(r => r.Duration.TotalSeconds);
                    var minDuration = successful.Min(r => r.Duration.TotalSeconds);
                    var maxDuration = successful.Max(r => r.Duration.TotalSeconds);
                    var totalInputTokens = successful.Sum(r => r.InputTokens);
                    var totalOutputTokens = successful.Sum(r => r.OutputTokens);
                    var totalTokens = successful.Sum(r => r.TotalTokens);
                    var requestsPerSecond = totalRequests / totalDuration.TotalSeconds;

                    Console.WriteLine($"\nPerformance Metrics:");
                    Console.WriteLine($"  Requests/Second: {requestsPerSecond:F2}");
                    Console.WriteLine($"  Avg Response Time: {avgDuration:F2}s");
                    Console.WriteLine($"  Min Response Time: {minDuration:F2}s");
                    Console.WriteLine($"  Max Response Time: {maxDuration:F2}s");

                    Console.WriteLine($"\nToken Usage:");
                    Console.WriteLine($"  Total Input Tokens: {totalInputTokens:N0}");
                    Console.WriteLine($"  Total Output Tokens: {totalOutputTokens:N0}");
                    Console.WriteLine($"  Total Tokens: {totalTokens:N0}");
                    Console.WriteLine($"  Avg Tokens/Request: {(totalTokens / successful.Length):F0}");
                    Console.WriteLine($"  Actual Tokens/Min: {(totalTokens / (totalDuration.TotalMinutes)):F0}");
                    Console.WriteLine($"  Actual Requests/Min: {(totalRequests / (totalDuration.TotalMinutes)):F1}");
                }

                if (failed.Any())
                {
                    Console.WriteLine($"\n=== FAILED REQUESTS ({failed.Length}) ===");
                    foreach (var failure in failed.Take(10))
                    {
                        Console.WriteLine($"Request #{failure.RequestId}: {failure.ErrorMessage}");
                    }
                    if (failed.Length > 10)
                    {
                        Console.WriteLine($"... and {failed.Length - 10} more failures");
                    }
                }

                // write start and end time to console
                Console.WriteLine($"\nTest started at: {startTime.ToLocalTime()}");
                Console.WriteLine($"Test ended at: {endTime.ToLocalTime()}");
               
                //Console.WriteLine("\nPress any key to exit...");
                //Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

        }


        static async Task RunOptimizedThroughputLoadTest(string prompt, string endpoint, string apiKey, string deploymentName)
        {

            try
            {
                // Ask for rate limit information
                Console.WriteLine("Enter your Azure OpenAI Rate Limits:");
                Console.Write("  Tokens Per Minute (TPM) limit? (e.g., 150000): ");
                var tpmInput = Console.ReadLine();
                int tpmLimit = string.IsNullOrWhiteSpace(tpmInput) ? 150000 : int.Parse(tpmInput);

                Console.Write("  Requests Per Minute (RPM) limit? (e.g., 150): ");
                var rpmInput = Console.ReadLine();
                int rpmLimit = string.IsNullOrWhiteSpace(rpmInput) ? 150 : int.Parse(rpmInput);

                Console.Write("  Expected tokens per request? (e.g., 9300): ");
                var tokensPerReqInput = Console.ReadLine();
                int expectedTokensPerRequest = string.IsNullOrWhiteSpace(tokensPerReqInput) ? 9300 : int.Parse(tokensPerReqInput);

                Console.WriteLine("\nTest Configuration:");
                Console.Write("  Total number of requests? (default 1000): ");
                var totalInput = Console.ReadLine();
                int totalRequests = string.IsNullOrWhiteSpace(totalInput) ? 1000 : int.Parse(totalInput);

                // Calculate optimal batch configuration
                var optimalConfig = CalculateOptimalBatch(tpmLimit, rpmLimit, expectedTokensPerRequest, totalRequests);

                Console.WriteLine("\n=== RECOMMENDED CONFIGURATION ===");
                Console.WriteLine($"Based on your limits:");
                Console.WriteLine($"  Max Requests/Min (RPM): {rpmLimit}");
                Console.WriteLine($"  Max Tokens/Min (TPM): {tpmLimit:N0}");
                Console.WriteLine($"  Expected Tokens/Request: {expectedTokensPerRequest:N0}");
                Console.WriteLine($"\nOptimal Settings:");
                Console.WriteLine($"  Batch Size: {optimalConfig.BatchSize}");
                Console.WriteLine($"  Batch Delay: {optimalConfig.DelaySeconds:F1}s");
                Console.WriteLine($"  Number of Batches: {optimalConfig.NumberOfBatches}");
                Console.WriteLine($"  Utilization - RPM: {optimalConfig.RpmUtilization:P0}, TPM: {optimalConfig.TpmUtilization:P0}");
                Console.WriteLine($"  Estimated Throughput: {optimalConfig.EstimatedThroughput:F2} requests/min");
                Console.WriteLine($"  Estimated Total Time: {optimalConfig.EstimatedTotalTime:F1}s\n");

                Console.Write("Use recommended settings? (Y/n): ");
                var useRecommended = Console.ReadLine();

                int batchSize, delaySeconds, numberOfBatches;

                if (string.IsNullOrWhiteSpace(useRecommended) || useRecommended.ToLower() == "y")
                {
                    batchSize = optimalConfig.BatchSize;
                    delaySeconds = optimalConfig.DelaySeconds;
                    numberOfBatches = optimalConfig.NumberOfBatches;
                    Console.WriteLine("✓ Using recommended settings\n");
                }
                else
                {
                    Console.Write("Requests per batch? (default 40): ");
                    var batchSizeInput = Console.ReadLine();
                    batchSize = string.IsNullOrWhiteSpace(batchSizeInput) ? 40 : int.Parse(batchSizeInput);

                    Console.Write("Delay between batches in seconds? (default 1): ");
                    var delayInput = Console.ReadLine();
                    delaySeconds = string.IsNullOrWhiteSpace(delayInput) ? 1 : int.Parse(delayInput);

                    numberOfBatches = (int)Math.Ceiling((double)totalRequests / batchSize);
                    Console.WriteLine("✓ Using custom settings\n");
                }

                AzureOpenAIClientOptions clientOptions = new();
                clientOptions.NetworkTimeout = TimeSpan.FromMinutes(5); // To prevent timeouts when obtaining long responses

                AzureOpenAIClient azureClient = new(
                    new Uri(endpoint),
                    new ApiKeyCredential(apiKey),
                    clientOptions
                );

                ChatClient chatClient = azureClient.GetChatClient(deploymentName);

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 16000,
                    Temperature = 1,
                    TopP = 1,
                    FrequencyPenalty = 0,
                    PresencePenalty = 0
                };

                Console.WriteLine("Starting batched execution...");
                Console.WriteLine("Press Ctrl+C to cancel\n");

                var startTime = DateTime.UtcNow;


                // Execute requests in batches
                var allResults = new List<RequestResult>();
                int requestCounter = 0;

                for (int batchNum = 0; batchNum < numberOfBatches; batchNum++)
                {
                    int requestsInThisBatch = Math.Min(batchSize, totalRequests - requestCounter);

                    Console.WriteLine($"\n=== Batch {batchNum + 1}/{numberOfBatches} ({requestsInThisBatch} requests) ===");

                    var batchTasks = new List<Task<RequestResult>>();

                    for (int i = 0; i < requestsInThisBatch; i++)
                    {
                        int requestId = requestCounter + i + 1;
                        batchTasks.Add(SendRequestAsync(chatClient, prompt, options, requestId));
                    }

                    // Wait for all tasks in this batch to complete
                    var batchResults = await Task.WhenAll(batchTasks);
                    allResults.AddRange(batchResults);

                    requestCounter += requestsInThisBatch;

                    var batchSuccessful = batchResults.Count(r => r.Success);
                    var batchFailed = batchResults.Count(r => !r.Success);
                    Console.WriteLine($"\nBatch {batchNum + 1} Complete - Success: {batchSuccessful}, Failed: {batchFailed}");

                    // Add delay between batches (except after the last batch)
                    if (batchNum < numberOfBatches - 1 && delaySeconds > 0)
                    {
                        Console.WriteLine($"Waiting {delaySeconds} seconds before next batch...");
                        await Task.Delay(delaySeconds * 1000);
                    }
                }

                var endTime = DateTime.UtcNow;
                var totalDuration = endTime - startTime;
                var results = allResults.ToArray();

                // Calculate statistics
                var successful = results.Where(r => r.Success).ToArray();
                var failed = results.Where(r => !r.Success).ToArray();

                Console.WriteLine("\n=== FINAL RESULTS ===");
                Console.WriteLine($"Total Requests: {totalRequests}");
                Console.WriteLine($"Successful: {successful.Length}");
                Console.WriteLine($"Failed: {failed.Length}");
                Console.WriteLine($"Total Duration: {totalDuration.TotalSeconds:F2} seconds");
                Console.WriteLine($"Number of Batches: {numberOfBatches}");
                Console.WriteLine($"Batch Size: {batchSize}");

                if (successful.Any())
                {
                    var avgDuration = successful.Average(r => r.Duration.TotalSeconds);
                    var minDuration = successful.Min(r => r.Duration.TotalSeconds);
                    var maxDuration = successful.Max(r => r.Duration.TotalSeconds);
                    var totalInputTokens = successful.Sum(r => r.InputTokens);
                    var totalOutputTokens = successful.Sum(r => r.OutputTokens);
                    var totalTokens = successful.Sum(r => r.TotalTokens);
                    var requestsPerSecond = totalRequests / totalDuration.TotalSeconds;

                    Console.WriteLine($"\nPerformance Metrics:");
                    Console.WriteLine($"  Requests/Second: {requestsPerSecond:F2}");
                    Console.WriteLine($"  Avg Response Time: {avgDuration:F2}s");
                    Console.WriteLine($"  Min Response Time: {minDuration:F2}s");
                    Console.WriteLine($"  Max Response Time: {maxDuration:F2}s");

                    Console.WriteLine($"\nToken Usage:");
                    Console.WriteLine($"  Total Input Tokens: {totalInputTokens:N0}");
                    Console.WriteLine($"  Total Output Tokens: {totalOutputTokens:N0}");
                    Console.WriteLine($"  Total Tokens: {totalTokens:N0}");
                    Console.WriteLine($"  Avg Tokens/Request: {(totalTokens / successful.Length):F0}");
                    Console.WriteLine($"  Actual Tokens/Min: {(totalTokens / (totalDuration.TotalMinutes)):F0}");
                    Console.WriteLine($"  Actual Requests/Min: {(totalRequests / (totalDuration.TotalMinutes)):F1}");
                }

                if (failed.Any())
                {
                    Console.WriteLine($"\n=== FAILED REQUESTS ({failed.Length}) ===");
                    foreach (var failure in failed.Take(10))
                    {
                        Console.WriteLine($"Request #{failure.RequestId}: {failure.ErrorMessage}");
                    }
                    if (failed.Length > 10)
                    {
                        Console.WriteLine($"... and {failed.Length - 10} more failures");
                    }
                }

                // Console.WriteLine("\n=== Sample Response ===");
                // if (successful.Any())
                // {
                //     var sample = successful.First();
                //     Console.WriteLine($"Request #{sample.RequestId}");
                //     Console.WriteLine($"Duration: {sample.Duration.TotalSeconds:F2}s");
                //     Console.WriteLine($"Tokens: {sample.TotalTokens}");
                //     //Console.WriteLine($"Response (first 500 chars):\n{sample.Response?.Substring(0, Math.Min(500, sample.Response?.Length ?? 0))}...\n");
                // }

                //Console.WriteLine("\nPress any key to exit...");
                //Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

        }

        static async Task<RequestResult> SendRequestAsync(ChatClient chatClient, string prompt, ChatCompletionOptions options, int requestId)
        {
            var result = new RequestResult { RequestId = requestId };
            var requestStart = DateTime.UtcNow;

            try
            {
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(prompt),
                };

                var completion = await chatClient.CompleteChatAsync(messages, options);
                var completionText = completion.Value.Content[0].Text;
                var usage = completion.Value.Usage;

                result.Duration = DateTime.UtcNow - requestStart;
                result.Success = true;
                result.Response = completionText;
                result.InputTokens = usage.InputTokenCount;
                result.OutputTokens = usage.OutputTokenCount;
                result.TotalTokens = usage.TotalTokenCount;

                Console.Write($"✓ {requestId}-{result.Duration.TotalSeconds:F1}s. ");
            }
            catch (Exception ex)
            {
                result.Duration = DateTime.UtcNow - requestStart;
                result.Success = false;
                result.ErrorMessage = ex.Message;
                Console.Write($"✗ {requestId} ");
            }

            return result;
        }

        static BatchConfig CalculateOptimalBatch(int tpmLimit, int rpmLimit, int expectedTokensPerRequest, int totalRequests)
        {
            // Calculate max requests we can send per minute based on TPM
            int maxRequestsPerMinByTPM = tpmLimit / expectedTokensPerRequest;
            
            // The actual RPM we can use is limited by both RPM and TPM
            int effectiveRpm = Math.Min(rpmLimit, maxRequestsPerMinByTPM);
            
            // Safety factor to stay under limits (90% utilization for better throughput)
            double safetyFactor = 0.90;
            int safeRpm = (int)(effectiveRpm * safetyFactor);
            
            // Calculate optimal batch size and delay
            // Goal: Maximize throughput while staying under limits
            
            // Strategy: Fill the 60-second window as much as possible
            // batch_size * (60 / (delay + avg_request_time)) should approach safeRpm
            
            int batchSize;
            int delaySeconds;
            double avgRequestTime = 5.0; // seconds (conservative estimate)
            
            if (safeRpm >= totalRequests)
            {
                // We can send all requests in one batch or close to it
                batchSize = totalRequests;
                delaySeconds = 0;
            }
            else
            {
                // Calculate how many requests we can fit in 60 seconds
                // accounting for request time and delays
                
                // Try different batch configurations and pick the best
                double bestThroughput = 0;
                int bestBatchSize = 1;
                int bestDelay = 0;
                
                // Test batch sizes from 1 to safeRpm
                for (int testBatch = 1; testBatch <= Math.Min(safeRpm, 100); testBatch++)
                {
                    // Calculate minimum delay needed to stay under limits
                    // batchSize requests per cycle, need to stay under safeRpm per minute
                    double cyclesPerMinute = (double)safeRpm / testBatch;
                    double secondsPerCycle = 60.0 / cyclesPerMinute;
                    double minDelay = Math.Max(0, secondsPerCycle - avgRequestTime);
                    
                    // Round delay to integer seconds
                    int testDelay = (int)Math.Ceiling(minDelay);
                    
                    // Calculate actual throughput with this configuration
                    double cycleTime = avgRequestTime + testDelay;
                    double actualThroughput = (testBatch * 60.0) / cycleTime;
                    
                    // Ensure we don't exceed limits
                    if (actualThroughput <= safeRpm && actualThroughput > bestThroughput)
                    {
                        bestThroughput = actualThroughput;
                        bestBatchSize = testBatch;
                        bestDelay = testDelay;
                    }
                }
                
                batchSize = bestBatchSize;
                delaySeconds = bestDelay;
            }
            
            // Ensure batch size is at least 1
            batchSize = Math.Max(1, batchSize);
            
            int numberOfBatches = (int)Math.Ceiling((double)totalRequests / batchSize);
            
            // Calculate utilization and throughput
            double requestsPerBatch = batchSize;
            double tokensPerBatch = requestsPerBatch * expectedTokensPerRequest;
            double batchesPerMinute = 60.0 / (delaySeconds + 5); // +5s estimated for batch execution
            
            double estimatedRpm = Math.Min(requestsPerBatch * batchesPerMinute, effectiveRpm);
            double estimatedTpm = estimatedRpm * expectedTokensPerRequest;
            
            double rpmUtilization = estimatedRpm / rpmLimit;
            double tpmUtilization = estimatedTpm / tpmLimit;
            
            // Estimate total time (batch time + delays)
            double estimatedBatchExecutionTime = 5; // seconds per batch (conservative estimate)
            double estimatedTotalTime = (numberOfBatches * estimatedBatchExecutionTime) + 
                                       ((numberOfBatches - 1) * delaySeconds);
            
            return new BatchConfig
            {
                BatchSize = batchSize,
                DelaySeconds = delaySeconds,
                NumberOfBatches = numberOfBatches,
                RpmUtilization = rpmUtilization,
                TpmUtilization = tpmUtilization,
                EstimatedThroughput = estimatedRpm,
                EstimatedTotalTime = estimatedTotalTime,
                EffectiveRpm = effectiveRpm,
                SafeRpm = safeRpm
            };
        }
    }

    class BatchConfig
    {
        public int BatchSize { get; set; }
        public int DelaySeconds { get; set; }
        public int NumberOfBatches { get; set; }
        public double RpmUtilization { get; set; }
        public double TpmUtilization { get; set; }
        public double EstimatedThroughput { get; set; }
        public double EstimatedTotalTime { get; set; }
        public int EffectiveRpm { get; set; }
        public int SafeRpm { get; set; }
    }

    class RequestResult
    {
        public int RequestId { get; set; }
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
        public string? Response { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
