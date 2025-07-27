Imports System.Net.Sockets
Imports System.Threading
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
''' Client for the sockets based TcpServer class
''' </summary> 

' This needs to be able to run on .NET 4.7.2 hence the custom buffer pool

Public Class TcpClient

    Implements IDisposable

    Public Event OnConnect()
    Public Event OnDisconnect()
    Public Event OnDataReceived(data As Byte())
    Public Event OnStreamDataReceived(data As Byte())
    Public Event OnExceptionOccurred(exception As Exception)

    Public StreamingMode As Boolean = False

    Private _cts As CancellationTokenSource
    Private _tcpClient As Sockets.TcpClient
    Private _isDisconnecting As Boolean = False

    Private _bytesReceived As Long
    Private _bytesSent As Long
    Private _packetsReceived As Long
    Private _connectionStartTime As DateTime
    Private disposedValue As Boolean
    'Private Shared ReadOnly _smallBufferPool As New SimpleBufferPool(4096)
    'Private Shared ReadOnly _mediumBufferPool As New SimpleBufferPool(65536)
    'Private Shared ReadOnly _largeBufferPool As New SimpleBufferPool(1048576)
    Private ReadOnly _receiveBuffer(65535) As Byte ' 64KB
    Private ReadOnly _workingBuffer As New MemoryStream()
    Private ReadOnly _lockObject As New Object()
    Private ReadOnly _packetDelimiter As String
    Private ReadOnly _delimiter As Byte()
    Private ReadOnly _delimiterLps As Integer()

    Public Property MaxBufferSize As Integer = 50 * 1024 * 1024 ' 50 MB max buffer; exceeding this will kick the client
    Public Property MaxPacketSize As Integer = 10 * 1024 * 1024 ' 10 MB

    Public ReadOnly Property Statistics As ClientStatistics
        Get
            Return New ClientStatistics With {
                .BytesReceived = _bytesReceived,
                .BytesSent = _bytesSent,
                .PacketsReceived = _packetsReceived,
                .ConnectionDuration = DateTime.Now - _connectionStartTime
            }
        End Get
    End Property

    Private Function GetPooledBuffer(size As Integer) As Byte()
        If size <= 4096 Then
            Return BufferPools.Small.Rent()
        ElseIf size <= 65536 Then
            Return BufferPools.Medium.Rent()
        ElseIf size <= 1048576 Then
            Return BufferPools.Large.Rent()
        Else
            Return New Byte(size - 1) {}
        End If
    End Function

    Private Sub ReturnPooledBuffer(buffer As Byte())
        If buffer Is Nothing Then Return

        Select Case buffer.Length
            Case 4096
                BufferPools.Small.Return(buffer)
            Case 65536
                BufferPools.Medium.Return(buffer)
            Case 1048576
                BufferPools.Large.Return(buffer)
        End Select
    End Sub

    Public Sub New(delimiter As String)
        If String.IsNullOrEmpty(delimiter) Then
            ' Default delimiter if not passed
            delimiter = "{}"
        End If

        _packetDelimiter = delimiter
        _delimiter = Encoding.UTF8.GetBytes(delimiter)
        _delimiterLps = ComputeLpsArray(_delimiter)
        _connectionStartTime = Now
    End Sub

    Public ReadOnly Property IsConnected As Boolean
        Get
            If _tcpClient Is Nothing OrElse _tcpClient.Client Is Nothing Then
                Return False
            End If

            Dim tcpSocket As Socket = _tcpClient.Client

            ' Poll(1, SelectMode.SelectRead) will return True if the connection is closed, reset, or terminated
            If tcpSocket.Poll(1, SelectMode.SelectRead) AndAlso tcpSocket.Available = 0 Then
                Return False
            End If

            Return tcpSocket.Connected
        End Get
    End Property

#Region "# Client Methods #"

    Public Sub StartStreaming()
        ThrowIfDisposed()
        StreamingMode = True
        If _tcpClient IsNot Nothing Then
            _tcpClient.Client.NoDelay = True
            _tcpClient.Client.ReceiveBufferSize = 64 * 1024
        End If
    End Sub

    Public Sub StopStreaming()
        ThrowIfDisposed()
        StreamingMode = False
        If _tcpClient IsNot Nothing Then
            _tcpClient.Client.NoDelay = False
            _tcpClient.Client.ReceiveBufferSize = 64 * 1024
        End If
    End Sub

    Public Sub SendStreamData(data As Byte())
        ThrowIfDisposed()
        If Not StreamingMode Then
            Throw New InvalidOperationException("Client is not in streaming mode")
        End If

        Try
            _tcpClient.Client.Send(data, 0, data.Length, SocketFlags.None)
            Interlocked.Add(_bytesSent, data.Length)
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            Disconnect()
        End Try
    End Sub

    Public Sub SendData(data As String)
        ThrowIfDisposed()
        SendData(Encoding.UTF8.GetBytes(data))
    End Sub

    Public Sub SendData(data As Byte())
        ThrowIfDisposed()
        SyncLock _lockObject
            If _tcpClient Is Nothing OrElse Not _tcpClient.Connected Then
                Throw New InvalidOperationException("Client is not connected.")
            End If

            Try
                Using bufferStream As New MemoryStream()
                    bufferStream.Write(data, 0, data.Length)
                    Dim delimiterBytes = Encoding.UTF8.GetBytes(_packetDelimiter)
                    bufferStream.Write(delimiterBytes, 0, delimiterBytes.Length)
                    Dim packetData = bufferStream.ToArray()
                    _tcpClient.Client.Send(packetData, SocketFlags.None)
                    Interlocked.Add(_bytesSent, packetData.Length)
                End Using
            Catch ex As Exception
                RaiseEvent OnExceptionOccurred(ex)
            End Try
        End SyncLock
    End Sub

    Private Const CONNECTION_REFUSED As Integer = 10061
    Public Async Function ConnectAsync(host As String, port As Integer) As Task(Of Boolean)
        ThrowIfDisposed()

        Try
            _cts = New CancellationTokenSource()
            _tcpClient = New Sockets.TcpClient(AddressFamily.InterNetwork)
            _connectionStartTime = Now

            Dim ipAddress As IPAddress = Nothing
            If Not IPAddress.TryParse(host, ipAddress) Then
                RaiseEvent OnExceptionOccurred(New Exception("Invalid IP address. Please check your input"))
                Return False
            End If

            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, True)
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, True)
            _tcpClient.Client.SendTimeout = 30000
            _tcpClient.Client.ReceiveTimeout = 30000

            Await _tcpClient.ConnectAsync(ipAddress, port)

            Dim incomingDataHandlerThread As New Thread(Sub() IncomingDataHandler(_cts.Token)) With {
                .IsBackground = True,
                .Name = "IncomingDataHandler_Thread"
            }
            incomingDataHandlerThread.Start()

            RaiseEvent OnConnect()
            Return True
        Catch socketEx As SocketException
            If socketEx.ErrorCode = CONNECTION_REFUSED Then
                Disconnect()
                Return False
            Else
                RaiseEvent OnExceptionOccurred(socketEx)
                Disconnect()
                Return False
            End If
        Catch ex As Exception
            RaiseEvent OnExceptionOccurred(ex)
            Disconnect()
            Return False
        End Try
    End Function

    Public Sub Disconnect()
        SyncLock _lockObject
            If _isDisconnecting OrElse disposedValue Then
                Return
            End If

            _isDisconnecting = True
        End SyncLock

        If _tcpClient IsNot Nothing Then
            StopStreaming()
            Try
                _tcpClient.Close()
            Catch ex As SocketException
                ' Ignore any socket exceptions
            Catch ex As Exception
                RaiseEvent OnExceptionOccurred(ex)
            Finally
                _cts.Cancel()
                _tcpClient.Dispose()
                RaiseEvent OnDisconnect()
            End Try
        End If

        _isDisconnecting = False
    End Sub

#End Region

#Region " # Thread Operations & Helper Methods #   "

    Private Sub IncomingDataHandler(cancellationToken As CancellationToken)
        If cancellationToken.IsCancellationRequested Then
            Exit Sub
        End If

        Try
            While Not cancellationToken.IsCancellationRequested
                Try
                    If Not IsConnectionAlive() AndAlso Not cancellationToken.IsCancellationRequested Then
                        Exit While
                    End If

                    ' Check if data is available
                    If _tcpClient IsNot Nothing AndAlso _tcpClient.Available > 0 AndAlso IsConnectionAlive() Then
                        If StreamingMode Then
                            ' Streaming Mode: Process data immediately without buffering
                            ProcessStreamingData()
                        Else
                            ' Normal Mode: Use packet processing with delimiter detection
                            ProcessIncomingData(_workingBuffer)
                        End If
                    Else
                        ' Should probably use ManualResetEvent here
                        Thread.Sleep(1)
                    End If

                Catch socketEx As SocketException
                    ' Handle specific socket exceptions
                    If Not cancellationToken.IsCancellationRequested Then
                        RaiseEvent OnExceptionOccurred(socketEx)
                        Exit While
                    End If
                Catch ioEx As IOException
                    ' Handle IO exceptions (often wrapping socket exceptions)
                    If Not cancellationToken.IsCancellationRequested Then
                        RaiseEvent OnExceptionOccurred(ioEx)
                        Exit While
                    End If
                Catch ex As ObjectDisposedException
                    ' Socket was disposed, exit gracefully
                    Exit While
                Catch ex As Exception
                    If Not cancellationToken.IsCancellationRequested Then
                        RaiseEvent OnExceptionOccurred(ex)
                        Exit While
                    End If
                End Try
            End While
        Finally
            ' Always raise disconnect event and clean up
            If Not _isDisconnecting AndAlso Not cancellationToken.IsCancellationRequested Then
                RaiseEvent OnDisconnect()
                Disconnect()
            End If
        End Try
    End Sub

    Private Function IsConnectionAlive() As Boolean
        If _tcpClient Is Nothing OrElse Not _tcpClient.Connected Then
            Return False
        End If

        Try
            If _tcpClient.Client.Poll(100, SelectMode.SelectRead) AndAlso _tcpClient.Client.Available = 0 Then
                Return False
            End If
        Catch
            Return False
        End Try

        Return True
    End Function

    Private Sub ProcessStreamingData()
        ' Get a buffer from the pool for streaming
        Dim streamBuffer As Byte() = Nothing
        Try
            ' Determine how much data is available
            Dim bytesAvailable = Math.Min(_tcpClient.Available, 16384) ' 16KB chunks

            ' Get appropriately sized buffer from pool
            streamBuffer = GetPooledBuffer(bytesAvailable)

            ' Receive data
            Dim bytesRead = _tcpClient.Client.Receive(streamBuffer, 0, bytesAvailable, SocketFlags.None)

            If bytesRead > 0 Then
                ' Update statistics
                Interlocked.Add(_bytesReceived, bytesRead)

                ' Create exact-sized array for the event
                Dim data(bytesRead - 1) As Byte
                Buffer.BlockCopy(streamBuffer, 0, data, 0, bytesRead)

                ' Raise streaming event
                RaiseEvent OnStreamDataReceived(data)
            ElseIf bytesRead = 0 Then
                ' Connection closed by remote host
                Throw New SocketException(10054) ' Connection reset by peer
            End If

        Finally
            ' Return buffer to pool
            If streamBuffer IsNot Nothing Then
                ReturnPooledBuffer(streamBuffer)
            End If
        End Try
    End Sub

    Private Sub ProcessIncomingData(ByRef workingMemoryStream As MemoryStream)
        ' Reuse the same buffer for receiving
        Dim bytesAvailable = Math.Min(_tcpClient.Available, _receiveBuffer.Length)
        If bytesAvailable = 0 Then Return

        Dim bytesRead = _tcpClient.Client.Receive(_receiveBuffer, 0, bytesAvailable, SocketFlags.None)
        If bytesRead = 0 Then
            ' Connection closed by remote host
            Throw New SocketException(10054) ' Connection reset by peer
        End If

        ' Update statistics (thread-safe)
        Interlocked.Add(_bytesReceived, bytesRead)

        ' Write received data to working buffer
        SyncLock _workingBuffer
            workingMemoryStream.Write(_receiveBuffer, 0, bytesRead)

            ' Process any complete packets
            ProcessPackets(workingMemoryStream)
        End SyncLock
    End Sub

    Private Sub ProcessPackets(ByRef bufferStream As MemoryStream)
        If bufferStream.Length > MaxBufferSize Then
            RaiseEvent OnExceptionOccurred(New Exception($"Buffer overflow: {bufferStream.Length} bytes"))
            Disconnect()
            Return
        End If

        Dim buffer = bufferStream.GetBuffer()
        Dim bufferLength = CInt(bufferStream.Length)
        Dim currentPosition = 0
        Dim processedData = False

        While currentPosition < bufferLength
            ' Use KMP to find delimiter
            Dim delimiterIndex = FindDelimiterKMP(buffer, bufferLength, currentPosition)

            If delimiterIndex < 0 Then Exit While

            Dim packetLength = delimiterIndex - currentPosition

            If packetLength > MaxPacketSize Then
                RaiseEvent OnExceptionOccurred(New Exception($"Packet too large: {packetLength} bytes"))
                Disconnect()
                Return
            End If

            ' Extract packet without allocation if possible
            If packetLength > 0 Then
                Dim packetData(packetLength - 1) As Byte
                System.Buffer.BlockCopy(buffer, currentPosition, packetData, 0, packetLength)
                RaiseEvent OnDataReceived(packetData)
                Interlocked.Increment(_packetsReceived)
            End If

            currentPosition = delimiterIndex + _delimiter.Length
            processedData = True
        End While

        ' Compact buffer if we processed data
        If processedData AndAlso currentPosition > 0 Then
            Dim remaining = bufferLength - currentPosition
            If remaining > 0 Then
                System.Buffer.BlockCopy(buffer, currentPosition, buffer, 0, remaining)
            End If
            bufferStream.SetLength(remaining)
            bufferStream.Position = remaining
        End If
    End Sub

    ' Comments for this are available in the server class
    Private Shared Function ComputeLpsArray(delimiter As Byte()) As Integer()
        Dim lps(delimiter.Length - 1) As Integer
        Dim length As Integer = 0
        Dim i As Integer = 1

        While i < delimiter.Length
            If delimiter(i) = delimiter(length) Then
                length += 1
                lps(i) = length
                i += 1
            Else
                If length <> 0 Then
                    length = lps(length - 1)
                Else
                    lps(i) = 0
                    i += 1
                End If
            End If
        End While

        Return lps
    End Function

    ' Comments for this are available in the server class
    Private Function FindDelimiterKMP(data() As Byte, dataLength As Integer, Optional startPos As Integer = 0) As Integer
        If _delimiter.Length = 0 OrElse dataLength - startPos < _delimiter.Length Then
            Return -1
        End If

        Dim i As Integer = startPos
        Dim j As Integer = 0

        While i < dataLength
            If _delimiter(j) = data(i) Then
                j += 1
                i += 1
            End If

            If j = _delimiter.Length Then
                Return i - j
            ElseIf i < dataLength AndAlso _delimiter(j) <> data(i) Then
                If j <> 0 Then
                    j = _delimiterLps(j - 1)
                Else
                    i += 1
                End If
            End If
        End While

        Return -1
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' Dispose managed resources
                Try
                    ' Cancel any ongoing operations
                    _cts?.Cancel()
                    _cts?.Dispose()

                    ' Close and dispose the TCP client
                    If _tcpClient IsNot Nothing Then
                        Try
                            If _tcpClient.Connected Then
                                _tcpClient.Close()
                            End If
                        Catch
                            ' Ignore exceptions during disposal
                        Finally
                            _tcpClient.Dispose()
                            _tcpClient = Nothing
                        End Try
                    End If

                    ' Dispose the working buffer
                    _workingBuffer?.Dispose()

                Catch ex As Exception
                    ' Should log here; though no need at the moment 
                End Try
            End If

            disposedValue = True
        End If
    End Sub

    Protected Overrides Sub Finalize()
        Dispose(False)
        MyBase.Finalize()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

    Private Sub ThrowIfDisposed()
        If disposedValue Then
            Throw New ObjectDisposedException(Me.GetType().Name)
        End If
    End Sub

#End Region

    Public Function GetUptimeFormatted() As String
        Return Statistics.ConnectionDuration.ToString("hh\:mm\:ss")
    End Function

End Class

Public Structure ClientStatistics

    Public BytesReceived As Long
    Public BytesSent As Long
    Public PacketsReceived As Long
    Public ConnectionDuration As TimeSpan

End Structure