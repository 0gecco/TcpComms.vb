Imports System.Collections.Concurrent
Imports System.Threading

Public Class SimpleBufferPool

    Private _rentedCount As Integer

    Private ReadOnly _buffers As New ConcurrentBag(Of Byte())
    Private ReadOnly _bufferSize As Integer
    Private ReadOnly _maxBuffers As Integer = 50

    Public Sub New(bufferSize As Integer)
        If bufferSize <= 0 Then
            Throw New ArgumentException("Buffer size must be greater than zero.", NameOf(bufferSize))
        End If
        _bufferSize = bufferSize
    End Sub

    Public Function Rent() As Byte()
        Dim buffer As Byte() = Nothing
        If _buffers.TryTake(buffer) Then
            Interlocked.Increment(_rentedCount)
            Return buffer
        End If

        ' Create new buffer if none available
        Interlocked.Increment(_rentedCount)
        Return New Byte(_bufferSize - 1) {}
    End Function

    Public Sub [Return](buffer As Byte())
        If buffer Is Nothing OrElse buffer.Length <> _bufferSize Then Return

        ' Clear sensitive data
        Array.Clear(buffer, 0, buffer.Length)

        ' Only return to pool if under limit
        If _buffers.Count < _maxBuffers Then
            _buffers.Add(buffer)
        End If
        Interlocked.Decrement(_rentedCount)
    End Sub

    ' Exposing some maybe useful internals
    Public ReadOnly Property RentedCount As Integer
        Get
            Return _rentedCount
        End Get
    End Property

    Public ReadOnly Property AvailableCount As Integer
        Get
            Return _buffers.Count
        End Get
    End Property

End Class