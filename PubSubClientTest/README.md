# OPC UA PubSub Client

A comprehensive OPC UA client application with subscription and write capabilities, built with .NET 6 and the OPC Foundation's .NET library.

## Features

### 🔌 **Flexible Server Connection**
- Connect to any OPC UA server via command line argument or interactive prompt
- Automatic retry with user-friendly error handling
- Support for different OPC UA endpoint URLs

### 📊 **Node Management**
- Browse and discover all variable nodes on the server
- Display node information including:
  - Write access permissions (`[WRITABLE]` / `[READ-ONLY]`)
  - Data types
  - Subscription status
- Filter nodes by namespace
- List writable nodes separately

### 📡 **Real-time Data Monitoring**
- Subscribe to individual nodes or entire namespaces
- Live data monitoring with timestamps
- Two monitoring modes:
  - **Subscription-based**: Event-driven updates
  - **Polling mode**: Configurable interval polling
- Toggle data display on/off during live monitoring

### ✍️ **Write Operations**
- Write values to writable nodes
- Automatic data type conversion for:
  - Boolean, Integers (Int16/32/64, UInt16/32/64)
  - Floating point (Float, Double)
  - String, DateTime, Byte
- Read-back confirmation after writes
- Current value display before writing

### ⚙️ **Advanced Configuration**
- Configurable data change filters and triggers
- Adjustable deadband settings  
- Customizable polling intervals
- Subscription management with queue settings

## Getting Started

### Prerequisites
- .NET 6.0 Runtime
- Access to an OPC UA server

### Installation
1. Clone this repository
2. Build the project:
   ```bash
   dotnet build
   ```

### Usage

#### Basic Usage
```bash
dotnet run
```
The application will prompt you to enter an OPC UA server endpoint URL.

#### With Command Line Argument
```bash
dotnet run "opc.tcp://your-server:4840/"
```

#### Example Server URLs
- `opc.tcp://localhost:4840/`
- `opc.tcp://192.168.1.100:4840/`
- `opc.tcp://localhost:4860/freeopcua/server/`

## Menu Options

1. **List all available nodes** - Browse all variable nodes on the server
2. **List nodes by namespace** - Filter nodes by namespace index
3. **List active subscriptions** - View currently subscribed nodes
4. **Subscribe to a node** - Add a node to monitoring
5. **Subscribe to nodes by namespace** - Subscribe to all nodes in a namespace
6. **Unsubscribe from a node** - Remove a node from monitoring
7. **Subscribe to all nodes** - Monitor all available nodes
8. **Unsubscribe from all nodes** - Stop monitoring all nodes
9. **View live data** - Real-time monitoring interface
10. **Toggle notification mode** - Switch between all updates vs changes only
11. **Read current values** - One-time read of subscribed nodes
12. **Check subscription status** - View detailed subscription information
13. **Change filter settings** - Configure data change filters
14. **Periodic polling** - Configure polling mode settings
15. **Rebuild internal dictionary** - Reset internal node tracking
16. **Write value to a node** - Write data to writable nodes
17. **List writable nodes only** - Show only nodes with write access
18. **Exit** - Close the application

## Live Data Monitoring

In live monitoring mode (Option 9), you can:
- Press `q` to return to main menu
- Press `s` to toggle data display on/off
- Press `p` to toggle between subscription and polling modes

## Write Operations

The application supports writing to nodes with write access:
1. Select "Write value to a node" from the menu
2. Choose from available writable nodes
3. View current value and data type
4. Enter new value (automatic type conversion)
5. Confirm successful write

## Configuration

The application automatically handles:
- Certificate management (auto-accepts untrusted certificates)
- Session timeouts and keep-alives
- Subscription queue management
- Error recovery and reconnection prompts

## Dependencies

- **OPCFoundation.NetStandard.Opc.Ua** - OPC UA client library
- **.NET 6.0** - Runtime environment

## Project Structure

```
PubSubClientTest/
├── Program.cs              # Main application code
├── PubSubClientTest.csproj  # Project configuration
├── .gitignore              # Git ignore rules
└── README.md               # This file
```

## Error Handling

The application includes comprehensive error handling:
- Connection failure retry with helpful hints
- Invalid URL format warnings
- Node access permission checks
- Data type conversion error messages
- Graceful session cleanup on exit

## Contributing

This is an educational/testing tool for OPC UA communication. Feel free to extend its functionality for your specific use cases.

## License

This project uses the OPC Foundation's .NET Standard library and follows its licensing terms. 