Imports System.Collections.Concurrent
Imports System.Net.Sockets
Imports System.Threading
Imports System.Buffers
Imports System.Text
Imports System.Net
Imports System.IO

'          oo   dP                            
'               88                            
' 88d888b. dP d8888P 88d88b. .d8888b. d888888b
' 88'  `88 88   88   88' `dP 88'  `88    .d8P'
' 88    88 88   88   88      88.  .88  .Y8P   
' dP    dP dP   dP   dP      `88888P' d888888P

' 88888           .d88b                                         8    
'   8   .d8b 88b. 8P    .d8b. 8d8b.d8b. 8d8b.d8b. d88b   Yb  dP 88b. 
'   8   8    8  8 8b    8' .8 8P Y8P Y8 8P Y8P Y8 `Yb.    YbdP  8  8 
'   8   `Y8P 88P' `Y88P `Y8P' 8   8   8 8   8   8 Y88P w   YP   88P' 
'            8                                                       
'            8

''' <summary>
''' Sockets based TCP server using threads instead of Async/Await approach
''' A massive class a day keeps the doctor away ;)
''' </summary>

Public Class TcpServer

    Implements IDisposable

    Public Event LogEvent(level As LogLevel, time As String, log As String)
    Public Event UploadedBytesCount(uploadedBytes As Long, formattedBytes As String)
    Public Event ClearClientList()

    Public Event OnClientConnect(socket As Integer)
    Public Event OnClientDisconnect(socket As Integer)
    Public Event OnDataReceived(socket As Integer, data As Byte())
    Public Event OnExceptionOccurred(ex As Exception)
    Public Event OnDataHandlerException(socket As Integer, ex As Exception)
    Public Event OnImageDataReceived(socket As Integer, imageBytes As Byte())
    Public Event OnStreamDataReceived(data As Byte())

    ' Client parameters
    Public Property ClientSendTimeout As Integer = 10 * 1000 ' 10 seconds
    Public Property ClientReceiveTimeout As Integer = 15 * 1000 ' 15 seconds
    Public Property ClientSendBufferSize As Integer = 1 * 1024 * 1024 ' 1 MB
    Public Property ClientReceiveBufferSize As Integer = 1 * 1024 * 1024 ' 1 MB

    ' Server parameters
    Public Property ImagePacketPrefix As String = "{IMAGE}"
    Public Property FilePacketPrefix As String = "{FILE}"
    Public Property LimitConnectionsPerMinute As Boolean = False
    Public Property MaxConnectionsPerMinute As Integer = 10
    Public Property MaxClients As Integer = 50
    Public Property MaxBufferSize As Integer = 50 * 1024 * 1024 ' 50 MB max buffer; exceeding this will kick the client
    Public Property MaxPacketSize As Integer = 10 * 1024 * 1024 ' 10 MB
    Public Property ReceiveTimeout As Integer = 15 * 1000 ' 15 seconds
    Public Property SendTimeout As Integer = 10 * 1000 ' 10 seconds

    Public Property PollInterval As Integer = 100 ' Call counts
    Public Property LogDebug As Boolean = False ' Set True to enable verbose logging of data events; should not be used when deployed
    Public Property BackLog As Integer = 5 ' Backlog for the listener
    Public Property StreamBufferSize As Integer = 64 * 1024 ' 64 KB; size of the sending buffer while streaming

    Private _disposed As Boolean
    Private _stopping As Boolean
    Private _rejectedConnections As Integer
    Private _connectionAttemptCounter As Integer
    Private _tcpListener As TcpListener
    Private _clientHandlerThread As Thread
    Private _clientIpAddresses As New ConcurrentDictionary(Of Integer, String)
    Private ReadOnly _clientsAvailable As New ManualResetEvent(False)
    Private ReadOnly _streamingClients As New ConcurrentDictionary(Of Integer, Boolean)
    Private ReadOnly _onlineClients As New ConcurrentDictionary(Of Integer, Integer) ' List of all connected clients indexed by socket numbers
    Private ReadOnly _disconnectFlags As New ConcurrentDictionary(Of Integer, Boolean)
    Private ReadOnly _connectionAttempts As New ConcurrentDictionary(Of Integer, DateTime)
    Private ReadOnly _disconnecting As New ConcurrentDictionary(Of Integer, Boolean)
    Private ReadOnly _sockets(MaxClients) As Socket
    Private ReadOnly _packetDelimiter As String
    Private ReadOnly _delimiterLps As Integer()
    Private ReadOnly _imagePrefix As Byte()
    Private ReadOnly _delimiter As Byte()
    Private ReadOnly _disposeLock As New Object()
    Private ReadOnly _clientIpAddressesLock As New Object()

    Public Sub New(delimiter As String)
        If String.IsNullOrEmpty(delimiter) Then
            Log(LogLevel.Warning, "No delimiter passed, defaulting to '{}'")
            delimiter = "{}"
        End If

        _connectionAttemptCounter = 0
        _rejectedConnections = 0
        _packetDelimiter = delimiter
        _disposed = False

        ThreadPool.SetMaxThreads(MaxClients + 1, MaxClients + 1)

        ' Pre-compute delimiters and cache the LPS array for the delimiter
        _delimiterLps = ComputeLpsArray(Encoding.UTF8.GetBytes(delimiter))
        _delimiter = Encoding.UTF8.GetBytes(_packetDelimiter)
        _imagePrefix = Encoding.UTF8.GetBytes(ImagePacketPrefix)
    End Sub

#Region "# Server Methods #"

    Public Sub Start(port As Integer)
        Try
            CheckDisposed()
            _stopping = False

            _tcpListener = New TcpListener(New IPEndPoint(IPAddress.Any, port))
            _tcpListener.Server.SendTimeout = SendTimeout
            _tcpListener.Server.ReceiveTimeout = ReceiveTimeout
            _tcpListener.Start(BackLog)

            _clientHandlerThread = New Thread(AddressOf HandleIncomingClients) With {
                .IsBackground = True,
                .Name = "ClientHandler_Thread"
            }
            _clientHandlerThread.Start() ' Handle incoming client on separate thread

            Log(LogLevel.Ok, $"Server started on port {port}.")
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            [Stop]() ' Ensure the server is stopped if it failed to start
        End Try
    End Sub

    Private Const STOP_DELAY As Integer = 50
    Public Sub [Stop]()
        Dim graceful = True
        Try
            CheckDisposed()

            _stopping = True
            DisconnectAllClients()

            ' Give time for clients to leave
            Thread.Sleep(STOP_DELAY)

            ' Stop accepting new clients
            If _clientHandlerThread IsNot Nothing AndAlso _clientHandlerThread.IsAlive Then
                If _clientHandlerThread.Join(5000) Then
                    Log(LogLevel.Warning, "Client handler thread stopped. No new clients will be accepted!")
                Else
                    RaiseEvent OnExceptionOccurred(New Exception("Client handler thread failed to stop"))
                    graceful = False
                End If
            End If

            ' Stop the listener
            If _tcpListener IsNot Nothing AndAlso _tcpListener.Server.IsBound Then
                _tcpListener.Stop()
                _tcpListener?.Dispose()
            End If

            If graceful Then
                Log(LogLevel.Info, "Server stopped.")
            Else
                Log(LogLevel.Warning, "Server was not stopped gracefully! Resources might not have been cleaned up")
            End If
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try
    End Sub

#End Region

#Region "# Server-to-Client Operations #"

    Public Sub StartStreaming(socketId As Integer)
        _streamingClients.TryAdd(socketId, True)
        If _sockets(socketId) IsNot Nothing Then
            _sockets(socketId).NoDelay = True ' Disable Nagle's algorithm
            _sockets(socketId).SendBufferSize = StreamBufferSize
        End If
    End Sub

    Public Sub StopStreaming(socketId As Integer)
        _streamingClients.TryRemove(socketId, Nothing)
        If _sockets(socketId) IsNot Nothing Then
            _sockets(socketId).NoDelay = False
            _sockets(socketId).SendBufferSize = ClientSendBufferSize
        End If
    End Sub

    Public Sub SendStreamData(socketId As Integer, data As Byte())
        If socketId < 0 OrElse socketId >= _sockets.Length Then
            Throw New ArgumentOutOfRangeException(NameOf(socketId))
        End If

        If Not _streamingClients.ContainsKey(socketId) Then
            Throw New InvalidOperationException("Client is not in streaming mode")
        End If

        Try
            _sockets(socketId).Send(data, 0, data.Length, SocketFlags.None)
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            DisconnectClient(socketId)
        End Try
    End Sub

    Public Sub SendImageData(targetSocket As Integer, imageBytes As Byte())
        Try
            Dim prefixedImageData As Byte() = Encoding.UTF8.GetBytes(ImagePacketPrefix).Concat(imageBytes).ToArray()
            SendDataInternal(targetSocket, prefixedImageData)
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try
    End Sub

    Public Sub SendFile(targetSocket As Integer, ByRef filePath As String)
        Try
            Dim uploadedBytes = New FileInfo(filePath).Length
            RaiseEvent UploadedBytesCount(uploadedBytes, FormatBytes(uploadedBytes))
            Dim postBuffer As Byte() = Encoding.UTF8.GetBytes(_packetDelimiter)
            _sockets(targetSocket).SendFile(filePath, Encoding.UTF8.GetBytes(FilePacketPrefix), postBuffer, TransmitFileOptions.UseDefaultWorkerThread)
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try
    End Sub

    Public Sub SendData(targetSocket As Integer, data As String)
        SendDataInternal(targetSocket, Encoding.UTF8.GetBytes(data))
    End Sub

    Public Sub SendData(targetSocket As Integer, data As Byte())
        SendDataInternal(targetSocket, data)
    End Sub

    Private Sub SendDataInternal(targetSocket As Integer, data As Byte())
        Dim uploadedBytes As Long = data.Length
        Try
            Using bufferStream As New MemoryStream
                bufferStream.Write(data, 0, data.Length)
                bufferStream.Write(Encoding.UTF8.GetBytes(_packetDelimiter), 0, _packetDelimiter.Length)
                _sockets(targetSocket).Send(bufferStream.ToArray(), 0, bufferStream.Length, SocketFlags.None)
            End Using
            RaiseEvent UploadedBytesCount(uploadedBytes, FormatBytes(uploadedBytes))
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            DisconnectClient(targetSocket)
        End Try
    End Sub

    Public Sub Broadcast(data As String, Optional excludeSocket As Integer? = Nothing)
        BroadcastInternal(Encoding.UTF8.GetBytes(data), excludeSocket)
    End Sub

    Public Sub Broadcast(data As Byte(), Optional excludeSocket As Integer? = Nothing)
        BroadcastInternal(data, excludeSocket)
    End Sub

    Private Sub BroadcastInternal(data As Byte(), Optional excludeSocket As Integer? = Nothing)
        For Each client In _onlineClients.Keys
            If _sockets(client) IsNot Nothing AndAlso _sockets(client).Connected AndAlso (excludeSocket Is Nothing OrElse client <> excludeSocket) Then
                SendData(client, data)
            End If
        Next
    End Sub

    Public Sub DisconnectClient(targetSocket As Integer)
        If Not _disconnecting.TryAdd(targetSocket, True) Then
            ' Another thread is already disconnecting this client
            Return
        End If

        Try
            ' Validate socket index
            If targetSocket < 0 OrElse targetSocket >= _sockets.Length Then
                Log(LogLevel.Error, $"Invalid socket index: {targetSocket}")
                Return
            End If

            ' Capture the socket reference once to avoid race conditions
            Dim socketToDisconnect = _sockets(targetSocket)
            If socketToDisconnect IsNot Nothing Then
                SyncLock socketToDisconnect

                    ' Double-check it's still the same socket after acquiring lock
                    If _sockets(targetSocket) Is socketToDisconnect AndAlso socketToDisconnect.Connected Then
                        Try
                            Dim ipAddress = GetClientIpAddress(targetSocket, withPort:=True)
                            socketToDisconnect.Disconnect(reuseSocket:=False)
                            socketToDisconnect.Close()

                            ' Remove IP address mapping
                            Dim ipRemovalSuccess = _clientIpAddresses.TryRemove(targetSocket, ipAddress)
                            If Not ipRemovalSuccess Then
                                Log(LogLevel.Error, $"Client {targetSocket} IP address not removed from list!")
                            End If
                        Catch socketEx As SocketException
                            If LogDebug Then Log(LogLevel.Debug, $"Socket exception during disconnect: {socketEx.Message}")
                        End Try
                    End If

                End SyncLock
            End If

            ' Remove from online clients and clean up
            Dim clientRemovalSuccess = _onlineClients.TryRemove(targetSocket, Nothing)
            If Not clientRemovalSuccess Then
                Log(LogLevel.Error, $"Client {targetSocket} not removed from online clients list! Attempting cleanup...")
                Dim cleanUp = CleanupOnlineClientsList()
                Debug.WriteLine($"Cleaned clients: {cleanUp}")
            End If

            RaiseEvent OnClientDisconnect(targetSocket)
            _sockets(targetSocket) = Nothing

        Catch ex As ObjectDisposedException
            If LogDebug Then Log(LogLevel.Debug, $"Client {targetSocket} socket already disposed")
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        Finally
            ' Always remove from disconnecting state
            _disconnecting.TryRemove(targetSocket, Nothing)
        End Try
    End Sub

    Public Sub DisconnectAllClients()
        For Each client In _onlineClients.Keys
            If _sockets(client) IsNot Nothing AndAlso _sockets(client).Connected Then
                DisconnectClient(client)
            End If
        Next

        _clientIpAddresses = New ConcurrentDictionary(Of Integer, String)

        RaiseEvent ClearClientList()
    End Sub

#End Region

#Region "# Thread Operations & Helper Methods #"

    Private Sub HandleIncomingClients()
        While Not _stopping
            Try
                ' Only process if there are pending connections
                If _tcpListener.Pending Then
                    ProcessIncomingClient()
                Else
                    ' Wait for signal or timeout
                    _clientsAvailable.WaitOne(10)
                End If
            Catch ex As Exception
                RaiseEvent OnExceptionOccurred(ex)
            End Try
        End While
    End Sub

    Private Sub ProcessIncomingClient()
        Try
            ' Check if new connections are allowed
            If Not IsNewClientAllowed() Then
                RejectConnection()
                Return
            End If

            ' Attempt to allocate a socket
            Dim assignedSocket = AllocateSocket()
            If assignedSocket = -1 Then
                RejectConnection()
                Return
            End If

            ' Perform client setup
            Dim clientSocket = _tcpListener.AcceptSocket()
            If Not SetupClientSocket(clientSocket) Then
                clientSocket.Close()
                Return
            End If

            InitializeClient(assignedSocket, clientSocket)
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try
    End Sub

    Private Sub RejectConnection()
        Try
            ' Accept the connection and immediately close it
            Dim tempSocket = _tcpListener.AcceptSocket()
            tempSocket.Close()
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try

        Interlocked.Increment(_rejectedConnections)
    End Sub

    Private Sub InitializeClient(socketId As Integer, ByRef clientSocket As Socket)
        SyncLock _sockets
            _sockets(socketId) = clientSocket
            _disconnectFlags(socketId) = False
        End SyncLock

        Dim clientIpAddress = clientSocket.RemoteEndPoint.ToString()
        _clientIpAddresses.TryAdd(socketId, clientIpAddress)

        ' Assign client to a new thread
        ThreadPool.QueueUserWorkItem(Sub() HandleIncomingData(socketId))

        RaiseEvent LogEvent(LogLevel.Info, DateTime.Now, $"Assigned client to socket {socketId};
                             client connected from {clientIpAddress}")
        RaiseEvent OnClientConnect(socketId)
    End Sub

    Private Function IsNewClientAllowed() As Boolean
        If Not LimitConnectionsPerMinute Then Return True
        Return IsConnectionAllowed()
    End Function

    Private Function IsConnectionAllowed() As Boolean
        ' Remove expired connection attempts
        Dim lastMinute As Date = DateTime.Now.AddMinutes(-1)
        Dim keysToRemove = _connectionAttempts.Where(Function(x) x.Value < lastMinute).Select(Function(x) x.Key).ToList()
        For Each key In keysToRemove
            _connectionAttempts.TryRemove(key, Nothing)
        Next

        ' Check if the limit has been reached
        If _connectionAttempts.Count >= MaxConnectionsPerMinute Then
            Log(LogLevel.Warning, $"Connection rejected due to rate limiting (Attempts/Minute)({_connectionAttempts.Count}/{MaxConnectionsPerMinute}).")
            Return False
        End If

        ' Add the current connection attempt with current timestamp
        Dim attemptKey = Interlocked.Increment(_connectionAttemptCounter)
        _connectionAttempts.TryAdd(attemptKey, DateTime.Now)
        Return True
    End Function

    Private Function AllocateSocket() As Integer
        SyncLock _clientHandlerThread ' Lock on '_sockets' should also work here
            ' First check if we have capacity
            If _onlineClients.Count >= MaxClients Then
                Log(LogLevel.Warning, $"Connection rejected. Server at maximum capacity ({MaxClients} clients)")
                Return -1
            End If

            ' Look for available socket slot
            For i = 0 To MaxClients - 1
                If _sockets(i) Is Nothing Then
                    _onlineClients.TryAdd(i, i)
                    Return i
                End If
            Next
        End SyncLock

        ' This should never happen due to count check above, but keep as safety check
        Log(LogLevel.Error, "No available sockets")
        Return -1
    End Function

    Private Function SetupClientSocket(ByRef clientSocket As Socket) As Boolean
        Try
            clientSocket.ReceiveBufferSize = ClientReceiveBufferSize
            clientSocket.SendBufferSize = ClientSendBufferSize
            clientSocket.ReceiveTimeout = ClientReceiveTimeout
            clientSocket.SendTimeout = ClientSendTimeout
            clientSocket.LingerState = New LingerOption(True, 0) ' Discard pending data
            Return True
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            Return False
        End Try
    End Function

    Private Sub HandleIncomingData(clientSocket As Integer)
        Dim pollCount = 0
        Dim bufferSize As Integer = ClientReceiveBufferSize
        Dim buffer As Byte() = ArrayPool(Of Byte).Shared.Rent(bufferSize)
        Dim bufferStream As New MemoryStream()

        If _sockets(clientSocket) Is Nothing Then
            ' Exit thread early if socket is actually dead
            Return
        End If

        Try
            While Not _stopping AndAlso Not _disconnectFlags.GetOrAdd(clientSocket, False)
                Try
                    ' Ensure client is still connected
                    If Not IsClientAlive(clientSocket, pollCount) Then
                        Exit While
                    End If

                    ' If no data is available, just skip ahead
                    If _sockets(clientSocket).Available <= 0 Then
                        Continue While
                    End If

                    ' Read available data
                    Dim availableData As Integer = _sockets(clientSocket).Receive(buffer, 0, buffer.Length, SocketFlags.None)
                    If availableData > 0 Then
                        If _streamingClients.ContainsKey(clientSocket) Then
                            ' Streaming Mode: Directly raise event
                            Dim streamData(availableData - 1) As Byte
                            System.Buffer.BlockCopy(buffer, 0, streamData, 0, availableData) ' Not optimal, but the event should be as simple as possible
                            RaiseEvent OnStreamDataReceived(streamData)
                        Else
                            ' Delimiter Mode: Append received data to buffer stream for processing
                            bufferStream.Write(buffer, 0, availableData)
                            ProcessClientData(clientSocket, bufferStream)
                        End If
                    End If
                Catch objectDisposedEx As ObjectDisposedException
                    ' Swallow it silently, this doesn't matter since client will be cleaned up
                Catch ex As Exception
                    RaiseEvent OnDataHandlerException(clientSocket, ex)
                    Exit While
                End Try

                Thread.Sleep(1)
            End While
        Catch objectDisposedEx As ObjectDisposedException
            pollCount = 0
            ' Swallow it silently, this doesn't matter since client will be cleaned up
        Catch ex As Exception
            pollCount = 0
            RaiseEvent OnDataHandlerException(clientSocket, ex)
        Finally
            ' Cleanup resources
            ArrayPool(Of Byte).Shared.Return(buffer, clearArray:=True)
            bufferStream.Dispose()
            DisconnectClient(clientSocket)
        End Try
    End Sub

    Private Function IsClientAlive(clientSocket As Integer, ByRef pollCount As Integer) As Boolean
        pollCount += 1

        If pollCount = PollInterval Then
            Try
                If _sockets(clientSocket) Is Nothing OrElse _disconnectFlags(clientSocket) Then
                    Return False
                End If

                If Not _sockets(clientSocket).Connected Then
                    Return False
                End If

                If _sockets(clientSocket).Poll(100, SelectMode.SelectRead) AndAlso _sockets(clientSocket).Available <= 0 Then
                    Return False
                End If
            Catch ex As ObjectDisposedException
                ' Socket was disposed
                Return False
            Catch ex As SocketException
                Return False
            Catch ex As Exception
                RaiseEvent OnExceptionOccurred(ex)
                Return False
            End Try

            pollCount = 0
        Else
            ' Always check for null socket or disconnect flag!
            If _sockets(clientSocket) Is Nothing OrElse _disconnectFlags(clientSocket) Then
                Return False
            End If
        End If

        Return True
    End Function

    Private Sub ProcessClientData(clientSocket As Integer, ByRef bufferStream As MemoryStream)
        ' Early check for buffer overflow
        If bufferStream.Length > MaxBufferSize Then
            HandleBufferOverflow(clientSocket)
            Return
        End If

        ' Rent a buffer from the pool instead of calling ToArray()
        Dim bufferLength As Long = bufferStream.Length
        Dim buffer As Byte() = ArrayPool(Of Byte).Shared.Rent(bufferLength)

        Try
            bufferStream.Position = 0
            bufferStream.Read(buffer, 0, bufferLength)

            Dim currentPosition As Integer = 0
            Dim processedData As Boolean = False

            ' Process all packets in the buffer in a single pass
            While currentPosition < bufferLength
                Dim delimiterIndex = FindDelimiter(buffer, _delimiter, bufferLength, currentPosition)

                If delimiterIndex < 0 Then
                    Exit While
                End If

                Dim packetLength = delimiterIndex - currentPosition

                If packetLength > MaxPacketSize Then
                    Log(LogLevel.Warning, $"Client {clientSocket}: Packet size exceeds maximum allowed size {FormatBytes(MaxPacketSize)}. Client will be kicked!")
                    _disconnectFlags(clientSocket) = True
                    Return
                End If

                ' Extract packet data - still need to allocate this unfortunately
                Dim packetData(packetLength - 1) As Byte
                System.Buffer.BlockCopy(buffer, currentPosition, packetData, 0, packetLength)

                ProcessPacket(clientSocket, packetData, _imagePrefix)

                currentPosition = delimiterIndex + _delimiter.Length
                processedData = True

                If LogDebug Then Log(LogLevel.Debug, $"Client({clientSocket}) packet processed: {packetLength} bytes")
            End While

            ' If we've processed data, rebuild the buffer with any remaining data
            If processedData Then
                bufferStream.SetLength(0)
                If currentPosition < bufferLength Then
                    bufferStream.Write(buffer, currentPosition, bufferLength - currentPosition)
                End If
            End If
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        Finally
            ' Always return the buffer to the pool
            ArrayPool(Of Byte).Shared.Return(buffer, clearArray:=True)
        End Try
    End Sub

    Private Sub HandleBufferOverflow(clientSocket As Integer)
        RaiseEvent LogEvent(LogLevel.Warning, Now, $"Client {clientSocket}: Buffer overflow - closing connection")
        _disconnectFlags(clientSocket) = True
    End Sub

    Private Sub ProcessPacket(clientSocket As Integer, ByRef packetData As Byte(), imagePrefix As Byte())
        Dim isImagePacket As Boolean = packetData.AsSpan(0, imagePrefix.Length).SequenceEqual(imagePrefix)
        If Not isImagePacket Then
            RaiseEvent OnDataReceived(clientSocket, packetData)
        Else
            Dim imageData As Byte() = ExtractImageData(packetData, imagePrefix.Length)
            RaiseEvent OnImageDataReceived(clientSocket, imageData)
        End If
    End Sub

    Private Shared Function ExtractImageData(ByRef packetData As Byte(), prefixLength As Integer) As Byte()
        ' Extracts image data by removing the image packet prefix
        Dim imageData(packetData.Length - prefixLength - 1) As Byte
        Buffer.BlockCopy(packetData, prefixLength, imageData, 0, imageData.Length)
        Return imageData
    End Function

#Region "# Delimiter Methods #"

    ' Computes the Longest Prefix Suffix (LPS) array used in KMP pattern matching algorithm
    Private Shared Function ComputeLpsArray(delimiter As Byte()) As Integer()
        Dim lps(delimiter.Length - 1) As Integer    ' Initialize the LPS array with zeros
        Dim length As Integer = 0                   ' Length of the previous longest prefix suffix
        Dim i As Integer = 1                        ' Start from second character

        ' Loop through the delimiter array to build the LPS array
        While i < delimiter.Length
            If delimiter(i) = delimiter(length) Then
                length += 1
                lps(i) = length
                i += 1
            Else
                If length <> 0 Then
                    ' Try the previous possible prefix length
                    length = lps(length - 1)
                Else
                    ' No match and length is 0, set lps[i] to 0
                    lps(i) = 0
                    i += 1
                End If
            End If
        End While

        Return lps

    End Function

    ' Finds the position of the first occurrence of the delimiter in the data using KMP algorithm
    Private Function FindDelimiter(data() As Byte, delimiter() As Byte, dataLength As Long, Optional startPos As Integer = 0) As Integer
        ' Return -1 if delimiter is empty or remaining data is smaller than delimiter
        If delimiter.Length = 0 OrElse dataLength - startPos < delimiter.Length Then
            Return -1
        End If

        Dim i As Integer = startPos  ' Index for data
        Dim j As Integer = 0         ' Index for delimiter

        ' Loop through data array
        While i < dataLength
            If delimiter(j) = data(i) Then
                ' Match found, move both pointers
                j += 1
                i += 1
            End If

            If j = delimiter.Length Then
                ' Full delimiter matched, return starting index
                Return i - j
            ElseIf i < dataLength AndAlso delimiter(j) <> data(i) Then
                If j <> 0 Then
                    ' Use LPS to skip characters in delimiter
                    j = _delimiterLps(j - 1)
                Else
                    ' No partial match, move to next character in data
                    i += 1
                End If
            End If
        End While

        ' Delimiter not found
        Return -1
    End Function

#End Region

#End Region ' This should probably be modularized, hehe

#Region "# Utilities #"

    Public Function CleanupOnlineClientsList(Optional forceCleanup As Boolean = False) As Integer
        Dim cleanedCount As Integer = 0
        Dim clientsToRemove As New List(Of Integer)

        Try
            ' First Pass: Identify clients that need to be removed
            For Each kvp In _onlineClients
                Dim socketIndex As Integer = kvp.Key
                Dim shouldRemove As Boolean = False

                If forceCleanup Then
                    shouldRemove = True
                Else
                    ' Check if socket index is valid
                    If socketIndex < 0 OrElse socketIndex >= _sockets.Length Then
                        shouldRemove = True
                    ElseIf _sockets(socketIndex) Is Nothing Then
                        shouldRemove = True
                    Else
                        ' Check if socket is actually connected
                        Try
                            If Not _sockets(socketIndex).Connected Then
                                shouldRemove = True
                            End If
                        Catch ex As ObjectDisposedException
                            ' Socket was disposed but reference still exists
                            shouldRemove = True
                        Catch ex As Exception
                            ' Any other socket exception means it's not usable
                            shouldRemove = True
                        End Try
                    End If
                End If

                If shouldRemove Then
                    clientsToRemove.Add(socketIndex)
                End If
            Next

            ' Second Pass: Remove the identified clients
            For Each socketIndex In clientsToRemove
                Try
                    ' Remove from online clients list
                    If _onlineClients.TryRemove(socketIndex, Nothing) Then
                        cleanedCount += 1

                        ' Also clean up related collections
                        _clientIpAddresses.TryRemove(socketIndex, Nothing)
                        _streamingClients.TryRemove(socketIndex, Nothing)
                        _disconnectFlags.TryRemove(socketIndex, Nothing)

                        ' Set socket to null if it exists
                        If socketIndex >= 0 AndAlso socketIndex < _sockets.Length Then
                            _sockets(socketIndex) = Nothing
                        End If

                        Log(LogLevel.Info, $"Cleaned up orphaned client entry for socket {socketIndex}")
                    End If
                Catch ex As Exception
                    Log(LogLevel.Warning, $"Failed to clean up client {socketIndex}: {ex.Message}")
                End Try
            Next

            If cleanedCount > 0 Then
                Log(LogLevel.Info, $"Cleanup completed: {cleanedCount} orphaned client entries removed")
                RaiseEvent ClearClientList() ' Notify UI to refresh client list
            End If

        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try

        Return cleanedCount
    End Function

    Public Shared Function FormatBytes(bytes As Long) As String
        If bytes < 1024 Then
            Return $"{bytes} bytes"
        End If

        Dim sizeSuffixes() As String = {"KB", "MB", "GB"}
        Dim sizeIndex As Integer = -1
        Dim size As Double = bytes

        While size >= 1024 AndAlso sizeIndex < sizeSuffixes.Length - 1
            sizeIndex += 1
            size /= 1024
        End While

        Dim suffix As String = sizeSuffixes(sizeIndex)
        Return $"{size:0.##} {suffix}"
    End Function

    Public Function GetClientIpAddress(socketIndex As Integer, withPort As Boolean) As String
        SyncLock _clientIpAddressesLock
            If socketIndex < 0 OrElse socketIndex >= _sockets.Length Then
                Return Nothing
            End If

            Dim socket = _sockets(socketIndex)
            If socket Is Nothing OrElse Not socket.Connected Then
                Return Nothing
            End If

            Try
                If Not withPort Then
                    Return Split(_sockets(socketIndex).RemoteEndPoint.ToString(), ":")(0)
                Else
                    Return _sockets(socketIndex).RemoteEndPoint.ToString()
                End If
            Catch ex As Exception
                RaiseEvent OnExceptionOccurred(ex)
                Return "N/A"
            End Try
        End SyncLock
    End Function

#End Region

    ' Wrapper for logging
    Private Sub Log(level As LogLevel, message As String)
        RaiseEvent LogEvent(level, Now, message)
    End Sub

    ' Exposing some useful internals
    Public ReadOnly Property IsRunning() As Boolean
        Get
            Return _tcpListener IsNot Nothing AndAlso _tcpListener.Server.IsBound AndAlso Not _stopping
        End Get
    End Property

    Public ReadOnly Property OnlineClients() As IReadOnlyList(Of Integer)
        Get
            Return _onlineClients.Keys.ToList()
        End Get
    End Property

    Public ReadOnly Property ClientIpAddresses() As IReadOnlyList(Of String)
        Get
            Return _clientIpAddresses.Values.ToList()
        End Get
    End Property

    Public ReadOnly Property Sockets() As Socket()
        Get
            Return _sockets
        End Get
    End Property

    Public ReadOnly Property OnlineClientCount() As Integer
        Get
            Return _onlineClients.Keys.Count
        End Get
    End Property

    Public ReadOnly Property RejectedConnections As Integer
        Get
            Return _rejectedConnections
        End Get
    End Property

#Region "# IDisposable Implementation #"

    Private Const DISPOSE_TIMEOUT As Integer = 5000
    Private Const DELAY As Integer = 100
    Protected Overridable Sub Dispose(disposing As Boolean)
        ' Use timeout for lock acquisition
        If Monitor.TryEnter(_disposeLock, DISPOSE_TIMEOUT) Then
            Try
                If Not _disposed Then
                    If disposing Then
                        Try
                            [Stop]()
                        Catch
                            _stopping = True ' Signal threads to stop
                            Thread.Sleep(DELAY) ' Give threads time to notice - only if Stop() failed
                            If _tcpListener IsNot Nothing Then
                                Try
                                    _tcpListener.Stop()
                                    _tcpListener.Dispose()
                                    _tcpListener = Nothing
                                Catch
                                    ' Ignore disposal errors
                                End Try
                            End If
                        Finally
                            DisconnectAllClients()
                        End Try

                        If _clientHandlerThread IsNot Nothing AndAlso _clientHandlerThread.IsAlive Then
                            Try
                                If Not _clientHandlerThread.Join(1000) Then ' Shorter timeout in dispose
                                    _clientHandlerThread.Interrupt()
                                End If
                            Catch
                                ' Ignore thread cleanup errors
                            End Try
                        End If

                        Try
                            _clientsAvailable?.Dispose()
                        Catch
                            ' Ignore disposal errors
                        End Try

                        ' Clean up remaining resources
                        For Each socket In _sockets
                            Try
                                socket?.Dispose()
                            Catch
                                ' Ignore disposal errors
                            End Try
                        Next

                        _clientIpAddresses.Clear()
                        _onlineClients.Clear()
                        _streamingClients.Clear()
                        _disconnectFlags.Clear()
                        _connectionAttempts.Clear()
                    End If
                    _disposed = True
                End If
            Finally
                Monitor.Exit(_disposeLock)
            End Try
        Else
            RaiseEvent LogEvent(LogLevel.Error, Now, "Dispose lock acquisition failed. Possible deadlock")
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

    Private Sub CheckDisposed()
        If Not _disposed Then
            Return
        End If

        Throw New ObjectDisposedException(GetType(TcpServer).FullName)
    End Sub

#End Region

End Class

Public Enum LogLevel

    Ok
    Info
    EventHappend
    Warning
    [Error]
    Debug

End Enum
