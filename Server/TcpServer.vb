Imports System.Collections.Concurrent
Imports System.Net.Sockets
Imports System.Threading
Imports System.Buffers
Imports System.Text
Imports System.Net
Imports System.IO

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

    Private Enum PacketType As Byte
        Delimited = 0
        Stream = 1
    End Enum

    Public Event LogEvent(level As LogLevel, time As String, log As String)
    Public Event UploadedBytesCount(uploadedBytes As Long, formattedBytes As String)
    Public Event ClearClientList()

    Public Event OnClientConnect(socket As Integer)
    Public Event OnClientDisconnect(socket As Integer)
    Public Event OnDataReceived(socket As Integer, data As Byte())
    Public Event OnExceptionOccurred(ex As Exception)
    Public Event OnDataHandlerException(socket As Integer, ex As Exception)
    Public Event OnImageDataReceived(socket As Integer, imageBytes As Byte())
    Public Event OnStreamDataReceived(clientSocket As Integer, data As Byte())

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
    Private ReadOnly _sockets() As Socket
    Private ReadOnly _packetDelimiter As String
    Private ReadOnly _delimiterLps As Integer()
    Private ReadOnly _imagePrefix As Byte()
    Private ReadOnly _delimiter As Byte()
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
        ' Remove the upper bound
        ReDim _sockets(MaxClients - 1)

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

            ' Handle incoming client on separate thread(s)
            _clientHandlerThread = New Thread(AddressOf HandleIncomingClients) With {
                .IsBackground = True,
                .Name = "ClientHandler_Thread"
            }
            _clientHandlerThread.Start()

            Log(LogLevel.Ok, $"Server started on port {port}.")
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            ' Ensure the server is stopped if it failed to start
            [Stop]()
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
            ' Disable Nagle's algorithm
            _sockets(socketId).NoDelay = True
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
        If Not OnlineClients.Contains(socketId) Then Return
        If socketId < 0 OrElse socketId >= _sockets.Length Then Throw New ArgumentOutOfRangeException(NameOf(socketId))
        If Not _streamingClients.ContainsKey(socketId) Then Throw New InvalidOperationException("Client is not in streaming mode")

        Dim s = _sockets(socketId)
        If s Is Nothing OrElse Not s.Connected Then Return

        Try
            ' Create and send header
            ' 1 byte type + 4 byte length (little endian)
            Dim header(4) As Byte
            header(0) = PacketType.Stream
            Buffer.BlockCopy(BitConverter.GetBytes(data.Length), 0, header, 1, 4)

            SyncLock s
                ' Send header
                SendAll(s, header, 0, 5)
                ' Send payload
                SendAll(s, data, 0, data.Length)
            End SyncLock
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            DisconnectClient(socketId)
        End Try
    End Sub

    Public Sub SendImageData(targetSocket As Integer, imageBytes As Byte())
        Dim s = _sockets(targetSocket)
        If s Is Nothing OrElse Not s.Connected Then Return

        Try
            Dim prefixedImageData As Byte() = Encoding.UTF8.GetBytes(ImagePacketPrefix).Concat(imageBytes).ToArray()
            SendDataInternal(targetSocket, prefixedImageData)
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try
    End Sub

    Public Sub SendFile(targetSocket As Integer, ByRef filePath As String)
        Dim s = _sockets(targetSocket)
        If s Is Nothing OrElse Not s.Connected Then Return

        Try
            Dim uploadedBytes = New FileInfo(filePath).Length
            RaiseEvent UploadedBytesCount(uploadedBytes, FormatBytes(uploadedBytes))
            Dim postBuffer As Byte() = Encoding.UTF8.GetBytes(_packetDelimiter)
            SyncLock s
                s.SendFile(filePath, Encoding.UTF8.GetBytes(FilePacketPrefix), postBuffer, TransmitFileOptions.UseDefaultWorkerThread)
            End SyncLock
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
        End Try
    End Sub

    Public Sub SendData(socketId As Integer, data As String)
        SendDataInternal(socketId, Encoding.UTF8.GetBytes(data))
    End Sub

    Public Sub SendData(socketId As Integer, data As Byte())
        SendDataInternal(socketId, data)
    End Sub

    Private Sub SendDataInternal(socketId As Integer, data As Byte())
        If Not OnlineClients.Contains(socketId) Then Return

        Dim s = _sockets(socketId)
        If s Is Nothing OrElse Not s.Connected Then Return

        Try
            ' Calculate total payload size (original data + delimiter)
            Dim payloadLength As Integer = data.Length + _delimiter.Length

            ' Packet layout:
            ' [0]    = PacketType.Delimited (1 byte)
            ' [1..4] = payloadLen (Int32 LE)
            ' [5..]  = data + delimiter

            ' Calculate total packet size including header
            Dim totalPacketLength As Integer = 1 + 4 + payloadLength
            Dim buf = ArrayPool(Of Byte).Shared.Rent(totalPacketLength)

            Try
                Dim pos As Integer = 0
                ' Write packet type identifier
                buf(pos) = PacketType.Delimited
                pos += 1

                ' Write payload length as 4-byte integer (Little Endian)
                Buffer.BlockCopy(BitConverter.GetBytes(payloadLength), 0, buf, pos, 4)
                pos += 4

                ' Write the actual data
                Buffer.BlockCopy(data, 0, buf, pos, data.Length)
                pos += data.Length

                ' Append the configured delimiter (used by the receiver's parser to detect packet boundaries)
                Buffer.BlockCopy(_delimiter, 0, buf, pos, _delimiter.Length)
                pos += _delimiter.Length

                SyncLock s
                    SendAll(s, buf, 0, totalPacketLength)
                End SyncLock
                RaiseEvent UploadedBytesCount(data.Length, FormatBytes(data.Length))
            Finally
                ArrayPool(Of Byte).Shared.Return(buf, clearArray:=True)
            End Try

        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            DisconnectClient(socketId)
        End Try
    End Sub

    Private Sub SendAll(socket As Socket, buffer As Byte(), offset As Integer, count As Integer)
        Dim sent As Integer = 0
        While sent < count
            Dim n As Integer
            Try
                n = socket.Send(buffer, offset + sent, count - sent, SocketFlags.None)
            Catch ex As SocketException
                RaiseEvent OnExceptionOccurred(New IOException($"Socket send failed: {ex.Message}", ex))
                Return ' Stop sending on error
            Catch ex As ObjectDisposedException
                RaiseEvent OnExceptionOccurred(New IOException("Socket disposed during send.", ex))
                Return
            End Try

            If n <= 0 Then
                RaiseEvent OnExceptionOccurred(New IOException("Socket closed during send."))
                Return
            End If

            sent += n
        End While
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
            ' Capture the socket reference once to avoid race conditions
            Dim socketToDisconnect = _sockets(targetSocket)

            ' Validate socket index
            If targetSocket < 0 OrElse targetSocket >= _sockets.Length Then
                Log(LogLevel.Error, $"Invalid socket index: {targetSocket}")
                Return
            End If

            ' Use single captured reference for check
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
                        Catch ex As Exception
                            RaiseEvent OnExceptionOccurred(ex)
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
            ' Verify we still own this slot
            If Not _onlineClients.ContainsKey(socketId) Then
                Log(LogLevel.Error, $"Socket {socketId} was deallocated before initialization")
                clientSocket.Close()
                Return
            End If

            ' Verify the slot is still available (should be Nothing)
            If _sockets(socketId) IsNot Nothing Then
                Log(LogLevel.Error, $"Socket {socketId} already occupied")
                clientSocket.Close()
                _onlineClients.TryRemove(socketId, Nothing)
                Return
            End If

            ' Assign the actual socket
            _sockets(socketId) = clientSocket
            _disconnectFlags(socketId) = False
        End SyncLock

        Dim clientIpAddress = clientSocket.RemoteEndPoint.ToString()

        ' Check for duplicate IP (potential race condition indicator)
        If _clientIpAddresses.Values.Contains(clientIpAddress) Then
            Log(LogLevel.Warning, $"IP {clientIpAddress} already connected on another socket")
        End If

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
        SyncLock _sockets
            ' First check if we have capacity
            If _onlineClients.Count >= MaxClients Then
                Log(LogLevel.Warning, $"Connection rejected. Server at maximum capacity ({MaxClients} clients)")
                Return -1
            End If

            ' Look for available socket slot
            For i = 0 To MaxClients - 1
                ' Check both socket and online client status
                If _sockets(i) Is Nothing AndAlso Not _onlineClients.ContainsKey(i) Then
                    ' Reserve the slot immediately
                    _onlineClients.TryAdd(i, i)
                    ' DON'T add a placeholder - just leave it as Nothing
                    ' The slot is now reserved via _onlineClients
                    Return i
                End If
            Next
        End SyncLock

        Log(LogLevel.Error, "No available sockets despite capacity check")
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

        ' Check if socket is valid
        If _sockets(clientSocket) Is Nothing Then
            ArrayPool(Of Byte).Shared.Return(buffer, clearArray:=True)
            Return
        End If

        ' Additional type check to be safe
        If TypeOf _sockets(clientSocket) IsNot Socket Then
            Log(LogLevel.Error, $"Socket {clientSocket} is not a valid Socket object")
            ArrayPool(Of Byte).Shared.Return(buffer, clearArray:=True)
            _onlineClients.TryRemove(clientSocket, Nothing)
            _sockets(clientSocket) = Nothing
            Return
        End If

        Try
            While Not _stopping AndAlso Not _disconnectFlags.GetOrAdd(clientSocket, False)
                Try
                    If Not IsClientAlive(clientSocket, pollCount) Then
                        Exit While
                    End If

                    If _sockets(clientSocket).Available <= 0 Then
                        Continue While
                    End If

                    Dim s = _sockets(clientSocket)
                    If s Is Nothing OrElse Not s.Connected Then Exit While

                    ' Read available data
                    Dim availableData As Integer
                    SyncLock s
                        availableData = s.Receive(buffer, 0, buffer.Length, SocketFlags.None)
                    End SyncLock
                    If availableData > 0 Then
                        ' ALWAYS append to buffer and process through unified system
                        bufferStream.Write(buffer, 0, availableData)
                        ProcessClientData(clientSocket, bufferStream)
                    End If
                Catch objDisposedEx As ObjectDisposedException
                    ' Exit silently
                Catch ex As Exception
                    RaiseEvent OnDataHandlerException(clientSocket, ex)
                    Exit While
                End Try

                Thread.Sleep(1)
            End While
        Catch ex As Exception
            pollCount = 0
            RaiseEvent OnDataHandlerException(clientSocket, ex)
        Finally
            ArrayPool(Of Byte).Shared.Return(buffer, clearArray:=True)
            bufferStream.Dispose()
            DisconnectClient(clientSocket)
        End Try
    End Sub

    Private Function IsClientAlive(clientSocket As Integer, ByRef pollCount As Integer) As Boolean
        pollCount += 1

        Dim s = _sockets(clientSocket)
        If s Is Nothing OrElse Not s.Connected Then Return False

        If pollCount = PollInterval Then
            Try
                If _sockets(clientSocket) Is Nothing OrElse _disconnectFlags(clientSocket) Then
                    Return False
                End If

                If Not _sockets(clientSocket).Connected Then
                    Return False
                End If

                SyncLock s
                    If s.Poll(100, SelectMode.SelectRead) AndAlso s.Available <= 0 Then
                        Return False
                    End If
                End SyncLock
            Catch objDisposedEx As ObjectDisposedException
                ' Socket was disposed
                Return False
            Catch socketEx As SocketException
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
        ' Need at least header and check for overflow
        If bufferStream.Length < 5 Then Return
        If bufferStream.Length >= MaxBufferSize Then HandleBufferOverflow(clientSocket)

        ' Cache before we mutate
        Dim totalLength As Integer = bufferStream.Length
        Dim buffer As Byte() = ArrayPool(Of Byte).Shared.Rent(totalLength)
        Try
            bufferStream.Position = 0
            bufferStream.Read(buffer, 0, bufferStream.Length)

            Dim currentPosition As Integer = 0
            Dim processedUpTo As Integer = 0

            While currentPosition + 5 <= totalLength
                ' Read packet header
                Dim packetType = CType(buffer(currentPosition), PacketType)
                Dim packetLength = BitConverter.ToInt32(buffer, currentPosition + 1)

                ' Check for incomplete data
                If currentPosition + 5 + packetLength > totalLength Then
                    Exit While
                End If
                currentPosition += 5

                ' Process based on packet type
                Select Case packetType
                    Case PacketType.Stream
                        ' Handle streaming data - CLIENT SPECIFIC
                        Dim streamData(packetLength - 1) As Byte
                        System.Buffer.BlockCopy(buffer, currentPosition, streamData, 0, packetLength)
                        If _streamingClients.ContainsKey(clientSocket) Then
                            ' This should be client-specific from server, not global!
                            ' Not critical for now, but will need re-work later if current implementation causes limitations
                            RaiseEvent OnStreamDataReceived(clientSocket, streamData)
                        End If

                    Case PacketType.Delimited
                        ' Process as delimited packet
                        Dim temp(packetLength - 1) As Byte
                        System.Buffer.BlockCopy(buffer, currentPosition, temp, 0, packetLength)
                        ProcessDelimitedData(clientSocket, temp, packetLength)
                End Select

                currentPosition += packetLength
                processedUpTo = currentPosition
            End While

            ' Keep the leftover (partial) bytes
            If processedUpTo > 0 Then
                Dim remaining = totalLength - processedUpTo
                bufferStream.SetLength(0)
                If remaining > 0 Then bufferStream.Write(buffer, processedUpTo, remaining)
            End If
        Catch ex As Exception
            RaiseEvent OnDataHandlerException(clientSocket, ex)
        Finally
            ArrayPool(Of Byte).Shared.Return(buffer, clearArray:=True)
        End Try
    End Sub

    Private Sub ProcessDelimitedData(clientSocket As Integer, buffer As Byte(), length As Integer)
        Dim currentPos = 0
        While currentPos < length
            Dim delimiterIndex = FindDelimiter(buffer, _delimiter, length, currentPos)
            If delimiterIndex < 0 Then Exit While

            Dim packetLength = delimiterIndex - currentPos
            If packetLength > 0 Then
                Dim packetData(packetLength - 1) As Byte
                System.Buffer.BlockCopy(buffer, currentPos, packetData, 0, packetLength)
                ProcessPacket(clientSocket, packetData, _imagePrefix)
            End If

            currentPos = delimiterIndex + _delimiter.Length
        End While
    End Sub

    Private Sub HandleBufferOverflow(clientSocket As Integer)
        RaiseEvent LogEvent(LogLevel.Warning, Now, $"Client {clientSocket}: Buffer overflow - closing connection")
        _disconnectFlags(clientSocket) = True
    End Sub

    Private Sub ProcessPacket(clientSocket As Integer, ByRef packetData As Byte(), imagePrefix As Byte())
        ' Check if it's an image packet first (legacy support)
        If packetData.Length >= imagePrefix.Length AndAlso packetData.AsSpan(0, imagePrefix.Length).SequenceEqual(imagePrefix) Then
            Dim imageData As Byte() = ExtractImageData(packetData, imagePrefix.Length)
            RaiseEvent OnImageDataReceived(clientSocket, imageData)
        Else
            ' Pass to packet handler for processing
            RaiseEvent OnDataReceived(clientSocket, packetData)
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

#End Region

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
        If _disposed Then Return

        ' Mark as disposed immediately to prevent re-entry
        _disposed = True

        If disposing Then
            ' Best-effort cleanup - don't let one failure prevent others
            Try
                [Stop]()
            Catch ex As Exception
                ' Log but continue cleanup
                RaiseEvent LogEvent(LogLevel.Error, Now, $"Error during Stop: {ex.Message}")
            End Try

            Try
                _clientsAvailable?.Dispose()
            Catch
            End Try

            For Each socket In _sockets
                Try
                    socket?.Dispose()
                Catch
                End Try
            Next

            ' Clear collections
            _clientIpAddresses.Clear()
            _onlineClients.Clear()
            _streamingClients.Clear()
            _disconnectFlags.Clear()
            _connectionAttempts.Clear()
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
