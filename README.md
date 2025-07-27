# TcpComms.vb
A high-performance, thread-based TCP server and client library for .NET (VB.NET), built with reliability and simplicity in mind.
The server runs on .NET 8 and the client on .NET 4.7.2.

## Features

- ✅ High-throughput TCP server and client
- ✅ Efficient buffer pooling (custom implementation)
- ✅ Delimiter-based packet handling (with KMP algorithm)
- ✅ Support for streaming large data
- ✅ Clean disconnection handling
- ✅ Easy integration via events

TCP Server Usage
================

1. Clone the repository.
2. Reference `TcpServer.vb` in your VB.NET project.
3. Set your packet delimiter and start communicating.
4. Subscribe to the required events for reading data from a client, and optional but recommended events:
   - OnDataReceived
   - OnExceptionOccurred
   - OnDataHandlerException
   - OnClientConnect
   - OnClientDisconnect
   - LogEvent

Example Server
--------------

```vbnet
Dim server As New TcpServer("{}")
server.Start(5555)
```
  
TCP Client Usage
================

1. Clone the repository.
2. Reference `TcpClient.vb`, `BufferPools.vb`, and `SimpleBufferPool.vb` in your VB.NET project (targeting .NET Framework 4.7.2).
3. Set the same packet delimiter as used on the server.
4. Connect to the server using `ConnectAsync`.
5. Use `SendData`, `SendStreamData`, or `StartStreaming` as needed.
6. Subscribe to events for handling of incoming data and connection status:
   - OnConnect
   - OnDisconnect
   - OnDataReceived
   - OnStreamDataReceived
   - OnExceptionOccurred

Example Client
--------------

```vbnet
Dim tcpClient As New TcpClient("{}")

AddHandler tcpClient.OnConnect, Sub()
    Console.WriteLine("Connected to server.")
End Sub

AddHandler tcpClient.OnDataReceived, Sub(data As Byte())
                                         Console.WriteLine("Received data: " & Encoding.UTF8.GetString(data))
                                     End Sub

AddHandler tcpClient.OnExceptionOccurred, Sub(ex As Exception)
                                              Console.WriteLine("Exception: " & ex.Message)
                                          End Sub

Await tcpClient.ConnectAsync("127.0.0.1", 5555)

' Send some data
tcpClient.SendData("Hello from client!")
```
