using Opc.Ua;
using Opc.Ua.Client;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

// Custom class to store node information including write access
public class NodeInfo
{
    public ReferenceDescription Reference { get; set; } = null!;
    public bool IsWritable { get; set; }
    public string DataType { get; set; } = "Unknown";
    public string DisplayName => Reference.DisplayName.Text;
    public NodeId NodeId => ExpandedNodeId.ToNodeId(Reference.NodeId, null);
}

class Program
{
    private static Session _session = null!;
    private static Subscription _subscription = null!;
    private static List<NodeInfo> _availableNodes = null!;
    private static Dictionary<string, MonitoredItem> _monitoredItems = new Dictionary<string, MonitoredItem>();
    private static bool _inLiveMonitoringMode = false;
    private static bool _displayData = true;
    private static bool _notifyOnAllUpdates = false;
    private static DataChangeTrigger _currentTrigger = DataChangeTrigger.StatusValueTimestamp;
    private static double _currentDeadband = 0.0;
    private static bool _usePollingMode = false;
    private static int _pollingIntervalMs = 1000;
    private static string _currentServerEndpoint = null!;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting OPC UA Client...");
        
        bool connectionSuccessful = false;
        bool firstAttempt = true;
        int attemptNumber = 1;
        
        // Keep trying to connect until successful or user exits
        while (!connectionSuccessful)
        {
            try
            {
                // Get server endpoint URL from command line or user input
                string endpointUrl;
                if (firstAttempt)
                {
                    endpointUrl = GetServerEndpointUrl(args);
                    firstAttempt = false;
                }
                else
                {
                    // For retry attempts, always prompt for new URL
                    endpointUrl = GetServerEndpointUrlForRetry();
                    if (endpointUrl == null) // User chose to exit
                    {
                        Console.WriteLine("Exiting application...");
                        return;
                    }
                }
                
                _currentServerEndpoint = endpointUrl;
                
                Console.WriteLine($"Attempt #{attemptNumber}");
                Console.WriteLine($"Server endpoint: {endpointUrl}");
                Console.WriteLine("Connecting to OPC UA server...");

                // Create application configuration
                var config = new ApplicationConfiguration()
                {
                    ApplicationName = "OPC UA PubSub Client",
                    ApplicationUri = "urn:OPCUAPubSubClient",
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier(),
                        TrustedPeerCertificates = new CertificateTrustList { StorePath = "pki/trusted" },
                        TrustedIssuerCertificates = new CertificateTrustList { StorePath = "pki/issuers" },
                        RejectedCertificateStore = new CertificateTrustList { StorePath = "pki/rejected" },
                        AutoAcceptUntrustedCertificates = true
                    },
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
                };

                // Validate configuration
                await config.Validate(ApplicationType.Client);

                // Create and connect to the server using ConfiguredEndpoint
                var selectedEndpoint = CoreClientUtils.SelectEndpoint(config, endpointUrl, false);
                var endpointConfiguration = EndpointConfiguration.Create(config);
                var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

                // Create the session
                _session = await Session.Create(config, configuredEndpoint, false, "OPC UA PubSub Client", 60000, null, null);
                Console.WriteLine("✓ Successfully connected to server!");
                
                connectionSuccessful = true;

                // Browse and find all variable nodes
                Console.WriteLine("\nBrowsing for variable nodes...");
                _availableNodes = await BrowseForVariables(_session, ObjectIds.ObjectsFolder);
                
                if (_availableNodes.Count > 0)
                {
                    Console.WriteLine($"Found {_availableNodes.Count} variable nodes available for subscription.");

                    // Create subscription
                    _subscription = new Subscription(_session.DefaultSubscription)
                    {
                        PublishingInterval = 1000, // 1 second
                        LifetimeCount = 0,
                        KeepAliveCount = 0,
                        MaxNotificationsPerPublish = 0,
                        Priority = 0,
                        PublishingEnabled = true
                    };

                    // Add subscription to session
                    _session.AddSubscription(_subscription);
                    _subscription.Create();

                    Console.WriteLine("Subscription created successfully!");
                    
                    // Start the interactive menu
                    await RunInteractiveMenu();
                }
                else
                {
                    Console.WriteLine("No variable nodes found to monitor.");
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                }

                // Close the session
                _session?.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Connection failed on attempt #{attemptNumber}");
                Console.WriteLine($"Error: {ex.Message}");
                
                // Show more specific error information
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Details: {ex.InnerException.Message}");
                }
                
                // Provide helpful hints based on common error types
                if (ex.Message.Contains("timeout") || ex.Message.Contains("timed out"))
                {
                    Console.WriteLine("Hint: The server might be unreachable or taking too long to respond.");
                }
                else if (ex.Message.Contains("refused") || ex.Message.Contains("connection refused"))
                {
                    Console.WriteLine("Hint: Check if the server is running and the port is correct.");
                }
                else if (ex.Message.Contains("resolve") || ex.Message.Contains("host"))
                {
                    Console.WriteLine("Hint: Check if the hostname/IP address is correct and reachable.");
                }
                
                Console.WriteLine();
                
                // Clean up any partial session
                try
                {
                    _session?.Close();
                    _session = null;
                }
                catch { }
                
                // Increment attempt counter for next try
                attemptNumber++;
                
                // If this was the first attempt and we have command line args, 
                // we should prompt for retry since the command line URL didn't work
                if (!connectionSuccessful)
                {
                    Console.WriteLine("Connection to OPC UA server failed.");
                    Console.WriteLine("Would you like to try a different server endpoint?");
                    Console.WriteLine();
                }
            }
        }
    }

    static string GetServerEndpointUrl(string[] args)
    {
        string endpointUrl = null;

        // Check if endpoint URL was provided as command line argument
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            endpointUrl = args[0];
            Console.WriteLine("Using endpoint URL from command line argument.");
        }
        else
        {
            // Prompt user for endpoint URL
            Console.WriteLine("=== OPC UA Server Connection ===");
            Console.WriteLine("Please enter the OPC UA server endpoint URL.");
            Console.WriteLine("Examples:");
            Console.WriteLine("  - opc.tcp://localhost:4840/");
            Console.WriteLine("  - opc.tcp://192.168.1.100:4840/");
            Console.WriteLine("  - opc.tcp://localhost:4860/freeopcua/server/");
            Console.WriteLine("  - opc.tcp://myserver.com:4840/opcua/server");
            Console.WriteLine();
            Console.Write("Enter server endpoint URL: ");
            
            endpointUrl = Console.ReadLine()?.Trim();
            
            // If user didn't enter anything, use default
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                endpointUrl = "opc.tcp://localhost:4860/freeopcua/server/";
                Console.WriteLine("No URL provided. Using default: " + endpointUrl);
            }
        }

        // Validate the URL format
        if (!IsValidOpcUaUrl(endpointUrl))
        {
            Console.WriteLine("Warning: The provided URL doesn't appear to be a valid OPC UA endpoint URL.");
            Console.WriteLine("OPC UA URLs typically start with 'opc.tcp://' followed by hostname:port");
            Console.WriteLine("Continuing anyway...");
        }

        return endpointUrl;
    }

    static string GetServerEndpointUrlForRetry()
    {
        while (true)
        {
            Console.WriteLine("=== Connection Retry ===");
            Console.WriteLine("Choose an option:");
            Console.WriteLine("1. Try a different server endpoint");
            Console.WriteLine("2. Exit application");
            Console.WriteLine();
            Console.Write("Enter your choice (1-2): ");
            
            var choice = Console.ReadLine()?.Trim();
            
            switch (choice)
            {
                case "1":
                    Console.WriteLine();
                    Console.WriteLine("Please enter a new OPC UA server endpoint URL.");
                    Console.WriteLine("Examples:");
                    Console.WriteLine("  - opc.tcp://localhost:4840/");
                    Console.WriteLine("  - opc.tcp://192.168.1.100:4840/");
                    Console.WriteLine("  - opc.tcp://localhost:4860/freeopcua/server/");
                    Console.WriteLine("  - opc.tcp://myserver.com:4840/opcua/server");
                    Console.WriteLine();
                    Console.Write("Enter server endpoint URL: ");
                    
                    var endpointUrl = Console.ReadLine()?.Trim();
                    
                    if (string.IsNullOrWhiteSpace(endpointUrl))
                    {
                        Console.WriteLine("No URL provided. Please try again.");
                        Console.WriteLine();
                        continue;
                    }
                    
                    // Validate the URL format
                    if (!IsValidOpcUaUrl(endpointUrl))
                    {
                        Console.WriteLine("Warning: The provided URL doesn't appear to be a valid OPC UA endpoint URL.");
                        Console.WriteLine("OPC UA URLs typically start with 'opc.tcp://' followed by hostname:port");
                        Console.WriteLine("Do you want to continue anyway? (y/n): ");
                        
                        var continueChoice = Console.ReadLine()?.Trim().ToLower();
                        if (continueChoice != "y" && continueChoice != "yes")
                        {
                            Console.WriteLine();
                            continue;
                        }
                    }
                    
                    return endpointUrl;
                    
                case "2":
                    return null; // Signal to exit
                    
                default:
                    Console.WriteLine("Invalid choice. Please enter 1 or 2.");
                    Console.WriteLine();
                    break;
            }
        }
    }

    static bool IsValidOpcUaUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Basic validation - should start with opc.tcp:// or opc.https://
        return url.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("opc.https://", StringComparison.OrdinalIgnoreCase);
    }

    static async Task RunInteractiveMenu()
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== OPC UA PubSub Client - Interactive Menu ===");
            Console.WriteLine($"Server: {_currentServerEndpoint}");
            Console.WriteLine($"Connection Status: CONNECTED | Subscription Status: {(_subscription.PublishingEnabled ? "ENABLED" : "DISABLED")}");
            Console.WriteLine($"Active Subscriptions: {_monitoredItems.Count}/{_availableNodes.Count}");
            Console.WriteLine();
            
            Console.WriteLine("1. List all available nodes");
            Console.WriteLine("2. List nodes by namespace (choose from available)");
            Console.WriteLine("3. List active subscriptions");
            Console.WriteLine("4. Subscribe to a node");
            Console.WriteLine("5. Subscribe to nodes by namespace (choose from available)");
            Console.WriteLine("6. Unsubscribe from a node");
            Console.WriteLine("7. Subscribe to all nodes");
            Console.WriteLine("8. Unsubscribe from all nodes");
            Console.WriteLine("9. View live data (real-time monitoring)");
            Console.WriteLine("10. Toggle notification mode (currently: " + (_notifyOnAllUpdates ? "ALL UPDATES" : "CHANGES ONLY") + ")");
            Console.WriteLine("11. Read current values of subscribed nodes");
            Console.WriteLine("12. Check subscription status");
            Console.WriteLine("13. Change filter settings");
            Console.WriteLine("14. Periodic polling");
            Console.WriteLine("15. Rebuild internal dictionary");
            Console.WriteLine("16. Write value to a node");
            Console.WriteLine("17. List writable nodes only");
            Console.WriteLine("18. Exit");
            Console.WriteLine();
            Console.Write("Select an option (1-18): ");

            var input = Console.ReadLine();
            
            switch (input)
            {
                case "1":
                    await ListAvailableNodes();
                    break;
                case "2":
                    await ListNodesByNamespace();
                    break;
                case "3":
                    ListActiveSubscriptions();
                    break;
                case "4":
                    await SubscribeToNode();
                    break;
                case "5":
                    await SubscribeToNodesByNamespace();
                    break;
                case "6":
                    await UnsubscribeFromNode();
                    break;
                case "7":
                    await SubscribeToAllNodes();
                    break;
                case "8":
                    await UnsubscribeFromAllNodes();
                    break;
                case "9":
                    await ViewLiveData();
                    break;
                case "10":
                    await ToggleNotificationMode();
                    break;
                case "11":
                    await ReadCurrentValues();
                    break;
                case "12":
                    await CheckSubscriptionStatus();
                    break;
                case "13":
                    await ChangeFilterSettings();
                    break;
                case "14":
                    await PeriodicPolling();
                    break;
                case "15":
                    await RebuildInternalDictionary();
                    break;
                case "16":
                    await WriteToNode();
                    break;
                case "17":
                    await ListWritableNodes();
                    break;
                case "18":
                    return;
                default:
                    Console.WriteLine("Invalid option. Press any key to continue...");
                    Console.ReadKey();
                    break;
            }
        }
    }

    static async Task ListAvailableNodes()
    {
        Console.WriteLine("\n=== Available Nodes ===");
        for (int i = 0; i < _availableNodes.Count; i++)
        {
            var node = _availableNodes[i];
            var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
            var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
            var subscribeStatus = isSubscribed ? "[SUBSCRIBED]" : "";
            Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {subscribeStatus}");
            Console.WriteLine($"    Data Type: {node.DataType}");
        }
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey();
    }

    static async Task ListNamespace1Nodes()
    {
        Console.WriteLine("\n=== Namespace 1 (ns=1) Nodes ===");
        var ns1Nodes = _availableNodes.Where(node => node.Reference.NodeId.ToString().StartsWith("ns=1;")).ToList();
        
        if (ns1Nodes.Count == 0)
        {
            Console.WriteLine("No nodes found in namespace 1 (ns=1).");
        }
        else
        {
            for (int i = 0; i < ns1Nodes.Count; i++)
            {
                var node = ns1Nodes[i];
                var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
                var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
                Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
            }
        }
        
        Console.WriteLine($"\nFound {ns1Nodes.Count} nodes in namespace 1.");
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ListNamespace2Nodes()
    {
        Console.WriteLine("\n=== Namespace 2 (ns=2) Nodes ===");
        var ns2Nodes = _availableNodes.Where(node => node.Reference.NodeId.ToString().StartsWith("ns=2;")).ToList();
        
        if (ns2Nodes.Count == 0)
        {
            Console.WriteLine("No nodes found in namespace 2 (ns=2).");
        }
        else
        {
            for (int i = 0; i < ns2Nodes.Count; i++)
            {
                var node = ns2Nodes[i];
                var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
                var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
                Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
            }
        }
        
        Console.WriteLine($"\nFound {ns2Nodes.Count} nodes in namespace 2.");
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static void ListActiveSubscriptions()
    {
        Console.WriteLine("\n=== Active Subscriptions ===");
        if (_monitoredItems.Count == 0)
        {
            Console.WriteLine("No active subscriptions.");
        }
        else
        {
            int i = 1;
            foreach (var kvp in _monitoredItems)
            {
                var item = kvp.Value;
                Console.WriteLine($"{i}. {item.DisplayName} ({item.StartNodeId})");
                i++;
            }
        }
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey();
    }

    static async Task SubscribeToNode()
    {
        Console.WriteLine("\n=== Subscribe to Node ===");
        for (int i = 0; i < _availableNodes.Count; i++)
        {
            var node = _availableNodes[i];
            var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
            var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
            Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
        }
        
        Console.Write($"Enter node number to subscribe (1-{_availableNodes.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int nodeIndex) && nodeIndex >= 1 && nodeIndex <= _availableNodes.Count)
        {
            var node = _availableNodes[nodeIndex - 1];
            
            if (_monitoredItems.ContainsKey(node.DisplayName))
            {
                Console.WriteLine("Node is already subscribed!");
            }
            else
            {
                await AddMonitoredItem(node);
                Console.WriteLine($"✓ Successfully subscribed to: {node.DisplayName}");
                Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
                Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");
            }
        }
        else
        {
            Console.WriteLine("Invalid node number.");
        }

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task UnsubscribeFromNode()
    {
        Console.WriteLine("\n=== Unsubscribe from Node ===");
        if (_monitoredItems.Count == 0)
        {
            Console.WriteLine("No active subscriptions to remove.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }

        var items = _monitoredItems.Values.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {items[i].DisplayName} ({items[i].StartNodeId})");
        }

        Console.Write($"Enter subscription number to remove (1-{items.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int itemIndex) && itemIndex >= 1 && itemIndex <= items.Count)
        {
            var item = items[itemIndex - 1];
            var itemName = item.DisplayName;
            await RemoveMonitoredItem(item);
            Console.WriteLine($"✓ Successfully unsubscribed from: {itemName}");
            Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
            Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");
        }
        else
        {
            Console.WriteLine("Invalid subscription number.");
        }

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task SubscribeToAllNodes()
    {
        Console.WriteLine("\n=== Subscribe to All Nodes ===");
        int addedCount = 0;
        
        foreach (var node in _availableNodes)
        {
            if (!_monitoredItems.ContainsKey(node.DisplayName))
            {
                await AddMonitoredItem(node);
                addedCount++;
                Console.WriteLine($"✓ Subscribed to: {node.DisplayName}");
            }
        }
        
        Console.WriteLine($"✓ Successfully subscribed to {addedCount} new nodes.");
        Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
        Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task UnsubscribeFromAllNodes()
    {
        Console.WriteLine("\n=== Unsubscribe from All Nodes ===");
        int removedCount = _monitoredItems.Count;
        
        var itemsToRemove = _monitoredItems.Values.ToList();
        foreach (var item in itemsToRemove)
        {
            Console.WriteLine($"✓ Removing: {item.DisplayName}");
            await RemoveMonitoredItem(item);
        }
        
        Console.WriteLine($"✓ Successfully unsubscribed from {removedCount} nodes.");
        Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
        Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ViewLiveData()
    {
        Console.WriteLine("\n=== Live Data Monitoring ===");
        if (_monitoredItems.Count == 0)
        {
            Console.WriteLine("No active subscriptions. Please subscribe to nodes first.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine($"Monitoring {_monitoredItems.Count} nodes in real-time...");
        Console.WriteLine("Subscribed nodes:");
        foreach (var item in _monitoredItems.Values)
        {
            Console.WriteLine($"  - {item.DisplayName} ({item.StartNodeId})");
        }
        Console.WriteLine($"Mode: {(_usePollingMode ? $"POLLING (every {_pollingIntervalMs}ms)" : "SUBSCRIPTION (change-based)")}");
        Console.WriteLine("Press 'q' to return to menu, 's' to toggle data display on/off, 'p' to toggle polling mode");
        Console.WriteLine(new string('=', 80));

        // Enable live monitoring mode and data display
        _inLiveMonitoringMode = true;
        _displayData = true;

        // Start polling task if enabled
        var pollingCancellation = new CancellationTokenSource();
        Task pollingTask = null;
        
        if (_usePollingMode)
        {
            pollingTask = StartPollingTask(pollingCancellation.Token);
        }

        while (true)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    break;
                else if (key.KeyChar == 's' || key.KeyChar == 'S')
                {
                    // Toggle data display immediately
                    _displayData = !_displayData;
                    
                    // Also toggle OPC UA publishing for completeness
                    _subscription.PublishingEnabled = _displayData;
                    _subscription.Modify();
                    
                    Console.WriteLine($"\n[SYSTEM] Data display {(_displayData ? "ENABLED" : "DISABLED")}");
                    Console.WriteLine($"[SYSTEM] OPC UA publishing {(_subscription.PublishingEnabled ? "ENABLED" : "DISABLED")}");
                }
                else if (key.KeyChar == 'p' || key.KeyChar == 'P')
                {
                    // Toggle polling mode
                    _usePollingMode = !_usePollingMode;
                    
                    if (_usePollingMode)
                    {
                        // Start polling
                        pollingCancellation?.Cancel();
                        pollingCancellation = new CancellationTokenSource();
                        pollingTask = StartPollingTask(pollingCancellation.Token);
                        Console.WriteLine($"\n[SYSTEM] Polling mode ENABLED (every {_pollingIntervalMs}ms)");
                    }
                    else
                    {
                        // Stop polling
                        pollingCancellation?.Cancel();
                        Console.WriteLine("\n[SYSTEM] Polling mode DISABLED (subscription-based)");
                    }
                }
            }
            
            await Task.Delay(100); // Small delay to prevent high CPU usage
        }

        // Stop polling when exiting
        pollingCancellation?.Cancel();
        if (pollingTask != null)
        {
            try
            {
                await pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
        }

        // Disable live monitoring mode when exiting
        _inLiveMonitoringMode = false;
        _displayData = true; // Reset for next time
        Console.WriteLine("\nExiting live monitoring mode...");
    }

    static async Task StartPollingTask(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_inLiveMonitoringMode && _displayData && _monitoredItems.Count > 0)
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    
                    foreach (var item in _monitoredItems.Values)
                    {
                        try
                        {
                            var value = _session.ReadValue(item.StartNodeId);
                            Console.WriteLine($"[POLL {timestamp}] {item.DisplayName}: {value.Value} (Status: {value.StatusCode})");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[POLL {timestamp}] Error reading {item.DisplayName}: {ex.Message}");
                        }
                    }
                }
                
                await Task.Delay(_pollingIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POLL ERROR] {ex.Message}");
                await Task.Delay(1000, cancellationToken); // Wait before retrying
            }
        }
    }

    static async Task AddMonitoredItem(NodeInfo node)
    {
        try
        {
            var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                DisplayName = node.DisplayName,
                StartNodeId = ExpandedNodeId.ToNodeId(node.Reference.NodeId, _session.NamespaceUris),
                AttributeId = Attributes.Value,
                MonitoringMode = MonitoringMode.Reporting,
                SamplingInterval = 500, // Faster sampling - 500ms
                QueueSize = 10, // Larger queue
                DiscardOldest = true
            };

            // Use current filter settings
            monitoredItem.Filter = new DataChangeFilter()
            {
                Trigger = _currentTrigger, // Use current trigger setting
                DeadbandType = (uint)DeadbandType.None,
                DeadbandValue = _currentDeadband // Use current deadband setting
            };

            monitoredItem.Notification += (item, e) =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var values = item.DequeueValues();
                
                // Only display formatted notifications when in live monitoring mode AND data display is enabled
                if (_inLiveMonitoringMode && _displayData)
                {
                    foreach (var value in values)
                    {
                        Console.WriteLine($"[{timestamp}] {item.DisplayName}: {value.Value} (Status: {value.StatusCode})");
                    }
                }
            };

            _subscription.AddItem(monitoredItem);
            _subscription.ApplyChanges();
            
            _monitoredItems[node.DisplayName] = monitoredItem;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding monitored item {node.DisplayName}: {ex.Message}");
        }
    }

    static async Task RemoveMonitoredItem(MonitoredItem item)
    {
        try
        {
            // Remove from subscription
            _subscription.RemoveItem(item);
            _subscription.ApplyChanges();
            
            // Find and remove from dictionary using DisplayName
            if (_monitoredItems.ContainsKey(item.DisplayName))
            {
                _monitoredItems.Remove(item.DisplayName);
            }
            else
            {
                Console.WriteLine($"Warning: Could not find {item.DisplayName} in monitoring dictionary");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing monitored item: {ex.Message}");
        }
    }

    static async Task SubscribeToNamespace2Node()
    {
        Console.WriteLine("\n=== Subscribe to Namespace 2 (ns=2) Node ===");
        var ns2Nodes = _availableNodes.Where(node => node.Reference.NodeId.ToString().StartsWith("ns=2;")).ToList();
        
        if (ns2Nodes.Count == 0)
        {
            Console.WriteLine("No nodes found in namespace 2 (ns=2).");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }

        for (int i = 0; i < ns2Nodes.Count; i++)
        {
            var node = ns2Nodes[i];
            var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
            var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
            Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
        }
        
        Console.Write($"Enter node number to subscribe (1-{ns2Nodes.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int nodeIndex) && nodeIndex >= 1 && nodeIndex <= ns2Nodes.Count)
        {
            var node = ns2Nodes[nodeIndex - 1];
            
            if (_monitoredItems.ContainsKey(node.DisplayName))
            {
                Console.WriteLine("Node is already subscribed!");
            }
            else
            {
                await AddMonitoredItem(node);
                Console.WriteLine($"✓ Successfully subscribed to: {node.DisplayName}");
                Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
                Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");
            }
        }
        else
        {
            Console.WriteLine("Invalid node number.");
        }

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task SubscribeToNamespace1Node()
    {
        Console.WriteLine("\n=== Subscribe to Namespace 1 (ns=1) Node ===");
        var ns1Nodes = _availableNodes.Where(node => node.Reference.NodeId.ToString().StartsWith("ns=1;")).ToList();
        
        if (ns1Nodes.Count == 0)
        {
            Console.WriteLine("No nodes found in namespace 1 (ns=1).");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }

        for (int i = 0; i < ns1Nodes.Count; i++)
        {
            var node = ns1Nodes[i];
            var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
            var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
            Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
        }
        
        Console.Write($"Enter node number to subscribe (1-{ns1Nodes.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int nodeIndex) && nodeIndex >= 1 && nodeIndex <= ns1Nodes.Count)
        {
            var node = ns1Nodes[nodeIndex - 1];
            
            if (_monitoredItems.ContainsKey(node.DisplayName))
            {
                Console.WriteLine("Node is already subscribed!");
            }
            else
            {
                await AddMonitoredItem(node);
                Console.WriteLine($"✓ Successfully subscribed to: {node.DisplayName}");
                Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
                Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");
            }
        }
        else
        {
            Console.WriteLine("Invalid node number.");
        }

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ToggleNotificationMode()
    {
        Console.WriteLine("\n=== Toggle Notification Mode ===");
        _notifyOnAllUpdates = !_notifyOnAllUpdates;
        Console.WriteLine($"✓ Notification mode toggled to: {( _notifyOnAllUpdates ? "ALL UPDATES" : "CHANGES ONLY")}");
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task<List<NodeInfo>> BrowseForVariables(Session session, NodeId nodeId)
    {
        var variables = new List<NodeInfo>();
        
        try
        {
            var browseDescription = new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)NodeClass.Object | (uint)NodeClass.Variable,
                ResultMask = (uint)BrowseResultMask.All
            };

            BrowseDescriptionCollection nodesToBrowse = new BrowseDescriptionCollection { browseDescription };
            session.Browse(
                null,
                null,
                0,
                nodesToBrowse,
                out BrowseResultCollection results,
                out DiagnosticInfoCollection diagnosticInfos
            );

            if (results != null && results.Count > 0 && results[0].References != null)
            {
                foreach (var reference in results[0].References)
                {
                    if (reference.NodeClass == NodeClass.Variable)
                    {
                        var nodeInfo = new NodeInfo
                        {
                            Reference = reference
                        };

                        // Check write access and get data type for variable nodes
                        try
                        {
                            var nodeId_expanded = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
                            
                            // Read attributes to get access level and data type
                            var attributesToRead = new ReadValueIdCollection
                            {
                                new ReadValueId
                                {
                                    NodeId = nodeId_expanded,
                                    AttributeId = Attributes.AccessLevel
                                },
                                new ReadValueId
                                {
                                    NodeId = nodeId_expanded,
                                    AttributeId = Attributes.DataType
                                }
                            };

                            var results_attr = session.Read(null, 0, TimestampsToReturn.Neither, attributesToRead, out var values, out var diagnostics);
                            
                            if (StatusCode.IsGood(results_attr.ServiceResult) && values.Count >= 2)
                            {
                                // Check access level for write capability
                                if (values[0].Value is byte accessLevel)
                                {
                                    nodeInfo.IsWritable = (accessLevel & AccessLevels.CurrentWrite) != 0;
                                }

                                // Get data type information
                                if (values[1].Value is NodeId dataTypeNodeId)
                                {
                                    // Try to get a readable data type name
                                    try
                                    {
                                        var dataTypeNode = session.ReadNode(dataTypeNodeId);
                                        nodeInfo.DataType = dataTypeNode.DisplayName.Text;
                                    }
                                    catch
                                    {
                                        nodeInfo.DataType = dataTypeNodeId.ToString();
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Could not read attributes for {reference.DisplayName}: {ex.Message}");
                            nodeInfo.IsWritable = false;
                            nodeInfo.DataType = "Unknown";
                        }

                        variables.Add(nodeInfo);
                    }
                    else if (reference.NodeClass == NodeClass.Object)
                    {
                        // Recursively browse objects for more variables
                        var childNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
                        var childVariables = await BrowseForVariables(session, childNodeId);
                        variables.AddRange(childVariables);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error browsing node {nodeId}: {ex.Message}");
        }

        return variables;
    }

    static async Task ReadCurrentValues()
    {
        Console.WriteLine("\n=== Read Current Values of Subscribed Nodes ===");
        if (_monitoredItems.Count == 0)
        {
            Console.WriteLine("No active subscriptions. Please subscribe to nodes first.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("Reading current values of subscribed nodes...");
        foreach (var item in _monitoredItems.Values)
        {
            try
            {
                var value = _session.ReadValue(item.StartNodeId);
                Console.WriteLine($"Node: {item.DisplayName} ({item.StartNodeId})");
                Console.WriteLine($"Value: {value.Value} (Status: {value.StatusCode})");
                Console.WriteLine($"Timestamp: {value.SourceTimestamp}");
                Console.WriteLine("---");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading {item.DisplayName}: {ex.Message}");
            }
        }

        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey();
    }

    static async Task CheckSubscriptionStatus()
    {
        Console.WriteLine("\n=== Check Subscription Status ===");
        
        Console.WriteLine("=== Subscription Information ===");
        Console.WriteLine($"Subscription ID: {_subscription.Id}");
        Console.WriteLine($"Publishing Enabled: {_subscription.PublishingEnabled}");
        Console.WriteLine($"Publishing Interval: {_subscription.PublishingInterval}ms");
        Console.WriteLine($"Monitored Items Count: {_subscription.MonitoredItemCount}");
        Console.WriteLine($"Session Connected: {_session.Connected}");
        Console.WriteLine($"Session Endpoint: {_session.Endpoint.EndpointUrl}");
        
        Console.WriteLine("\n=== Internal Dictionary Status ===");
        Console.WriteLine($"Dictionary Count: {_monitoredItems.Count}");
        Console.WriteLine($"Dictionary Keys: {string.Join(", ", _monitoredItems.Keys)}");
        
        Console.WriteLine("\n=== Actual OPC UA Monitored Items ===");
        if (_subscription.MonitoredItemCount > 0)
        {
            foreach (var item in _subscription.MonitoredItems)
            {
                Console.WriteLine($"OPC Item: {item.DisplayName} ({item.StartNodeId})");
                Console.WriteLine($"  Monitoring Mode: {item.MonitoringMode}");
                Console.WriteLine($"  Sampling Interval: {item.SamplingInterval}ms");
                Console.WriteLine($"  Queue Size: {item.QueueSize}");
                Console.WriteLine($"  Filter: {item.Filter?.GetType().Name ?? "None"}");
                if (item.Filter is DataChangeFilter dcf)
                {
                    Console.WriteLine($"  Trigger: {dcf.Trigger}");
                    Console.WriteLine($"  Deadband: {dcf.DeadbandValue}");
                }
                Console.WriteLine("  ---");
            }
        }
        else
        {
            Console.WriteLine("No OPC UA monitored items found.");
        }
        
        if (_monitoredItems.Count == 0 && _subscription.MonitoredItemCount > 0)
        {
            Console.WriteLine("\n⚠️  WARNING: OPC UA subscription has items but internal dictionary is empty!");
            Console.WriteLine("This suggests a tracking issue. Try option 18 to rebuild the dictionary.");
        }
        else if (_monitoredItems.Count > 0)
        {
            Console.WriteLine("\n=== Internal Dictionary Items ===");
            foreach (var kvp in _monitoredItems)
            {
                var item = kvp.Value;
                Console.WriteLine($"Dict Item: {item.DisplayName} ({item.StartNodeId})");
                Console.WriteLine($"  Key: {kvp.Key}");
                Console.WriteLine("  ---");
            }
        }

        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey();
    }

    static async Task ChangeFilterSettings()
    {
        Console.WriteLine("\n=== Change Filter Settings ===");
        Console.WriteLine("Current Filter: Trigger = " + _currentTrigger + ", Deadband = " + _currentDeadband);
        Console.WriteLine("1. Change Trigger");
        Console.WriteLine("2. Change Deadband");
        Console.WriteLine("3. Reset to default");
        Console.WriteLine("4. Exit");
        Console.WriteLine();
        Console.Write("Select an option (1-4): ");

        var input = Console.ReadLine();
        
        switch (input)
        {
            case "1":
                await ChangeTrigger();
                break;
            case "2":
                await ChangeDeadband();
                break;
            case "3":
                ResetToDefault();
                break;
            case "4":
                return;
            default:
                Console.WriteLine("Invalid option. Press any key to continue...");
                Console.ReadKey();
                break;
        }
    }

    static async Task ChangeTrigger()
    {
        Console.WriteLine("\n=== Change Filter Trigger ===");
        Console.WriteLine("1. StatusValueTimestamp (most sensitive)");
        Console.WriteLine("2. StatusValue (status or value changes)");
        Console.WriteLine("3. Status (status changes only)");
        Console.WriteLine("4. Exit");
        Console.WriteLine();
        Console.Write("Select an option (1-4): ");

        var input = Console.ReadLine();
        
        switch (input)
        {
            case "1":
                _currentTrigger = DataChangeTrigger.StatusValueTimestamp;
                break;
            case "2":
                _currentTrigger = DataChangeTrigger.StatusValue;
                break;
            case "3":
                _currentTrigger = DataChangeTrigger.Status;
                break;
            case "4":
                return;
            default:
                Console.WriteLine("Invalid option. Press any key to continue...");
                Console.ReadKey();
                break;
        }

        Console.WriteLine("✓ Filter trigger changed to: " + _currentTrigger);
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ChangeDeadband()
    {
        Console.WriteLine("\n=== Change Filter Deadband ===");
        Console.Write("Enter new deadband value (0.0 to 1.0): ");
        if (double.TryParse(Console.ReadLine(), out double newDeadband) && newDeadband >= 0.0 && newDeadband <= 1.0)
        {
            _currentDeadband = newDeadband;
            Console.WriteLine("✓ Filter deadband changed to: " + _currentDeadband);
        }
        else
        {
            Console.WriteLine("Invalid input. Deadband must be between 0.0 and 1.0.");
        }
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static void ResetToDefault()
    {
        _currentTrigger = DataChangeTrigger.StatusValueTimestamp;
        _currentDeadband = 0.0;
        Console.WriteLine("✓ Filter reset to default: Trigger = " + _currentTrigger + ", Deadband = " + _currentDeadband);
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task PeriodicPolling()
    {
        Console.WriteLine("\n=== Periodic Polling ===");
        Console.WriteLine("1. Enable polling");
        Console.WriteLine("2. Disable polling");
        Console.WriteLine("3. Change polling interval");
        Console.WriteLine("4. Exit");
        Console.WriteLine();
        Console.Write("Select an option (1-4): ");

        var input = Console.ReadLine();
        
        switch (input)
        {
            case "1":
                _usePollingMode = true;
                Console.WriteLine("✓ Polling mode enabled");
                break;
            case "2":
                _usePollingMode = false;
                Console.WriteLine("✓ Polling mode disabled");
                break;
            case "3":
                await ChangePollingInterval();
                break;
            case "4":
                return;
            default:
                Console.WriteLine("Invalid option. Press any key to continue...");
                Console.ReadKey();
                break;
        }

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ChangePollingInterval()
    {
        Console.WriteLine("\n=== Change Polling Interval ===");
        Console.Write("Enter new polling interval in milliseconds (1000ms = 1 second): ");
        if (int.TryParse(Console.ReadLine(), out int newInterval) && newInterval > 0)
        {
            _pollingIntervalMs = newInterval;
            Console.WriteLine("✓ Polling interval changed to: " + _pollingIntervalMs + "ms");
        }
        else
        {
            Console.WriteLine("Invalid input. Interval must be greater than 0.");
        }
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task RebuildInternalDictionary()
    {
        Console.WriteLine("\n=== Rebuild Internal Dictionary ===");
        Console.WriteLine($"Current OPC UA monitored items: {_subscription.MonitoredItemCount}");
        Console.WriteLine($"Current dictionary items: {_monitoredItems.Count}");
        
        // Clear the internal dictionary
        _monitoredItems.Clear();
        
        // Rebuild from actual OPC UA subscription
        foreach (var item in _subscription.MonitoredItems)
        {
            _monitoredItems[item.DisplayName] = item;
            Console.WriteLine($"✓ Added {item.DisplayName} to dictionary");
        }
        
        Console.WriteLine($"✓ Internal dictionary rebuilt successfully!");
        Console.WriteLine($"✓ Dictionary now contains {_monitoredItems.Count} items");
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ListNodesByNamespace()
    {
        Console.WriteLine("\n=== List Nodes by Namespace ===");
        
        // Get all unique namespaces from available nodes
        var namespaces = _availableNodes
            .Select(node => ExtractNamespaceIndex(node.Reference.NodeId.ToString()))
            .Where(ns => ns.HasValue)
            .Distinct()
            .OrderBy(ns => ns.Value)
            .ToList();
        
        if (namespaces.Count == 0)
        {
            Console.WriteLine("No namespaces found in available nodes.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }
        
        Console.WriteLine("Available namespaces:");
        for (int i = 0; i < namespaces.Count; i++)
        {
            var nsIndex = namespaces[i].Value;
            var nodeCount = _availableNodes.Count(node => ExtractNamespaceIndex(node.Reference.NodeId.ToString()) == nsIndex);
            Console.WriteLine($"{i + 1}. Namespace {nsIndex} (ns={nsIndex}) - {nodeCount} nodes");
        }
        
        Console.Write($"Select namespace to list (1-{namespaces.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int nsChoice) && nsChoice >= 1 && nsChoice <= namespaces.Count)
        {
            var selectedNs = namespaces[nsChoice - 1].Value;
            var nsNodes = _availableNodes.Where(node => ExtractNamespaceIndex(node.Reference.NodeId.ToString()) == selectedNs).ToList();
            
            Console.WriteLine($"\n=== Namespace {selectedNs} (ns={selectedNs}) Nodes ===");
            for (int i = 0; i < nsNodes.Count; i++)
            {
                var node = nsNodes[i];
                var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
                var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
                Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
            }
            Console.WriteLine($"\nFound {nsNodes.Count} nodes in namespace {selectedNs}.");
        }
        else
        {
            Console.WriteLine("Invalid namespace selection.");
        }
        
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task SubscribeToNodesByNamespace()
    {
        Console.WriteLine("\n=== Subscribe to Nodes by Namespace ===");
        
        // Get all unique namespaces from available nodes
        var namespaces = _availableNodes
            .Select(node => ExtractNamespaceIndex(node.Reference.NodeId.ToString()))
            .Where(ns => ns.HasValue)
            .Distinct()
            .OrderBy(ns => ns.Value)
            .ToList();
        
        if (namespaces.Count == 0)
        {
            Console.WriteLine("No namespaces found in available nodes.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }
        
        Console.WriteLine("Available namespaces:");
        for (int i = 0; i < namespaces.Count; i++)
        {
            var nsIndex = namespaces[i].Value;
            var nodeCount = _availableNodes.Count(node => ExtractNamespaceIndex(node.Reference.NodeId.ToString()) == nsIndex);
            var subscribedCount = _availableNodes.Count(node => 
                ExtractNamespaceIndex(node.Reference.NodeId.ToString()) == nsIndex && 
                _monitoredItems.ContainsKey(node.DisplayName));
            Console.WriteLine($"{i + 1}. Namespace {nsIndex} (ns={nsIndex}) - {nodeCount} nodes ({subscribedCount} subscribed)");
        }
        
        Console.Write($"Select namespace to subscribe to (1-{namespaces.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int nsChoice) && nsChoice >= 1 && nsChoice <= namespaces.Count)
        {
            var selectedNs = namespaces[nsChoice - 1].Value;
            var nsNodes = _availableNodes.Where(node => ExtractNamespaceIndex(node.Reference.NodeId.ToString()) == selectedNs).ToList();
            
            Console.WriteLine($"\n=== Namespace {selectedNs} (ns={selectedNs}) Nodes ===");
            for (int i = 0; i < nsNodes.Count; i++)
            {
                var node = nsNodes[i];
                var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
                var writeStatus = node.IsWritable ? "[WRITABLE]" : "[READ-ONLY]";
                Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {writeStatus} {(isSubscribed ? "[SUBSCRIBED]" : "")}");
            }
            
            Console.WriteLine($"\nOptions:");
            Console.WriteLine($"1. Subscribe to a specific node");
            Console.WriteLine($"2. Subscribe to all nodes in this namespace");
            Console.WriteLine($"3. Cancel");
            Console.Write("Select option (1-3): ");
            
            var option = Console.ReadLine();
            switch (option)
            {
                case "1":
                    Console.Write($"Enter node number to subscribe (1-{nsNodes.Count}): ");
                    if (int.TryParse(Console.ReadLine(), out int nodeIndex) && nodeIndex >= 1 && nodeIndex <= nsNodes.Count)
                    {
                        var node = nsNodes[nodeIndex - 1];
                        if (_monitoredItems.ContainsKey(node.DisplayName))
                        {
                            Console.WriteLine("Node is already subscribed!");
                        }
                        else
                        {
                            await AddMonitoredItem(node);
                            Console.WriteLine($"✓ Successfully subscribed to: {node.DisplayName}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Invalid node number.");
                    }
                    break;
                    
                case "2":
                    int addedCount = 0;
                    foreach (var node in nsNodes)
                    {
                        if (!_monitoredItems.ContainsKey(node.DisplayName))
                        {
                            await AddMonitoredItem(node);
                            addedCount++;
                            Console.WriteLine($"✓ Subscribed to: {node.DisplayName}");
                        }
                    }
                    Console.WriteLine($"✓ Successfully subscribed to {addedCount} new nodes in namespace {selectedNs}.");
                    break;
                    
                case "3":
                    Console.WriteLine("Cancelled.");
                    break;
                    
                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
            
            Console.WriteLine($"✓ Total active subscriptions: {_monitoredItems.Count}");
            Console.WriteLine($"✓ Subscription contains {_subscription.MonitoredItemCount} items");
        }
        else
        {
            Console.WriteLine("Invalid namespace selection.");
        }
        
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static int? ExtractNamespaceIndex(string nodeIdString)
    {
        // Extract namespace index from node ID string like "ns=1;i=1234" or "ns=2;s=SomeString"
        if (nodeIdString.StartsWith("ns="))
        {
            var parts = nodeIdString.Split(';');
            if (parts.Length > 0)
            {
                var nsPart = parts[0].Substring(3); // Remove "ns="
                if (int.TryParse(nsPart, out int nsIndex))
                {
                    return nsIndex;
                }
            }
        }
        return null;
    }

    static async Task WriteToNode()
    {
        Console.WriteLine("\n=== Write Value to Node ===");
        
        // Get all writable nodes
        var writableNodes = _availableNodes.Where(n => n.IsWritable).ToList();
        
        if (writableNodes.Count == 0)
        {
            Console.WriteLine("No writable nodes found.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            return;
        }
        
        Console.WriteLine("Available writable nodes:");
        for (int i = 0; i < writableNodes.Count; i++)
        {
            var node = writableNodes[i];
            Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId})");
            Console.WriteLine($"    Data Type: {node.DataType}");
        }
        
        Console.Write($"Select node to write to (1-{writableNodes.Count}): ");
        if (int.TryParse(Console.ReadLine(), out int nodeIndex) && nodeIndex >= 1 && nodeIndex <= writableNodes.Count)
        {
            var selectedNode = writableNodes[nodeIndex - 1];
            
            Console.WriteLine($"\nSelected node: {selectedNode.DisplayName}");
            Console.WriteLine($"Data type: {selectedNode.DataType}");
            
            // Read current value first
            try
            {
                var nodeId = ExpandedNodeId.ToNodeId(selectedNode.Reference.NodeId, _session.NamespaceUris);
                var currentValue = _session.ReadValue(nodeId);
                Console.WriteLine($"Current value: {currentValue.Value} (Status: {currentValue.StatusCode})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not read current value: {ex.Message}");
            }
            
            Console.Write("Enter new value: ");
            var newValueStr = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(newValueStr))
            {
                Console.WriteLine("No value entered. Operation cancelled.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }
            
            // Attempt to write the value
            try
            {
                await WriteValueToNode(selectedNode, newValueStr);
                Console.WriteLine("✓ Successfully wrote value to node!");
                
                // Read back the value to confirm
                await Task.Delay(500); // Small delay to ensure value is written
                var nodeId = ExpandedNodeId.ToNodeId(selectedNode.Reference.NodeId, _session.NamespaceUris);
                var confirmedValue = _session.ReadValue(nodeId);
                Console.WriteLine($"Confirmed value: {confirmedValue.Value} (Status: {confirmedValue.StatusCode})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to write value: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Invalid node selection.");
        }
        
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task WriteValueToNode(NodeInfo node, string valueString)
    {
        var nodeId = ExpandedNodeId.ToNodeId(node.Reference.NodeId, _session.NamespaceUris);
        
        // Determine the data type and convert the string value accordingly
        object convertedValue = ConvertStringToDataType(valueString, node.DataType);
        
        // Create the write value
        var writeValue = new WriteValue
        {
            NodeId = nodeId,
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(convertedValue))
        };
        
        var writeValues = new WriteValueCollection { writeValue };
        
        // Write the value
        var response = _session.Write(null, writeValues, out var results, out var diagnostics);
        
        if (StatusCode.IsBad(response.ServiceResult))
        {
            throw new Exception($"Write operation failed with status: {response.ServiceResult}");
        }
        
        if (results.Count > 0 && StatusCode.IsBad(results[0]))
        {
            throw new Exception($"Write failed for node: {results[0]}");
        }
    }

    static object ConvertStringToDataType(string valueString, string dataType)
    {
        // Handle common OPC UA data types
        switch (dataType?.ToLower())
        {
            case "boolean":
            case "bool":
                if (bool.TryParse(valueString, out bool boolValue))
                    return boolValue;
                // Also accept 1/0 for boolean
                if (int.TryParse(valueString, out int intBool))
                    return intBool != 0;
                throw new ArgumentException($"Cannot convert '{valueString}' to Boolean");
                
            case "int16":
            case "short":
                if (short.TryParse(valueString, out short shortValue))
                    return shortValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to Int16");
                
            case "int32":
            case "integer":
            case "int":
                if (int.TryParse(valueString, out int intValue))
                    return intValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to Int32");
                
            case "int64":
            case "long":
                if (long.TryParse(valueString, out long longValue))
                    return longValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to Int64");
                
            case "uint16":
                if (ushort.TryParse(valueString, out ushort ushortValue))
                    return ushortValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to UInt16");
                
            case "uint32":
                if (uint.TryParse(valueString, out uint uintValue))
                    return uintValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to UInt32");
                
            case "uint64":
                if (ulong.TryParse(valueString, out ulong ulongValue))
                    return ulongValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to UInt64");
                
            case "float":
            case "single":
                if (float.TryParse(valueString, out float floatValue))
                    return floatValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to Float");
                
            case "double":
                if (double.TryParse(valueString, out double doubleValue))
                    return doubleValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to Double");
                
            case "string":
            case "text":
                return valueString;
                
            case "datetime":
                if (DateTime.TryParse(valueString, out DateTime dateTimeValue))
                    return dateTimeValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to DateTime");
                
            case "byte":
                if (byte.TryParse(valueString, out byte byteValue))
                    return byteValue;
                throw new ArgumentException($"Cannot convert '{valueString}' to Byte");
                
            default:
                // For unknown types, try some common conversions
                Console.WriteLine($"Warning: Unknown data type '{dataType}', attempting automatic conversion...");
                
                // Try integer first
                if (int.TryParse(valueString, out int autoInt))
                    return autoInt;
                    
                // Try double
                if (double.TryParse(valueString, out double autoDouble))
                    return autoDouble;
                    
                // Try boolean
                if (bool.TryParse(valueString, out bool autoBool))
                    return autoBool;
                    
                // Default to string
                return valueString;
        }
    }

    static async Task ListWritableNodes()
    {
        Console.WriteLine("\n=== Writable Nodes Only ===");
        
        var writableNodes = _availableNodes.Where(n => n.IsWritable).ToList();
        
        if (writableNodes.Count == 0)
        {
            Console.WriteLine("No writable nodes found.");
        }
        else
        {
            Console.WriteLine($"Found {writableNodes.Count} writable nodes:");
            for (int i = 0; i < writableNodes.Count; i++)
            {
                var node = writableNodes[i];
                var isSubscribed = _monitoredItems.ContainsKey(node.DisplayName);
                Console.WriteLine($"{i + 1}. {node.DisplayName} ({node.Reference.NodeId}) {(isSubscribed ? "[SUBSCRIBED]" : "")}");
                Console.WriteLine($"    Data Type: {node.DataType}");
            }
        }
        
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey();
    }
}
