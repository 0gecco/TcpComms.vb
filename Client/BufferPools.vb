Public Module BufferPools

    Public ReadOnly Small As New SimpleBufferPool(4096)    ' 4 KB
    Public ReadOnly Medium As New SimpleBufferPool(65536)  ' 64 KB
    Public ReadOnly Large As New SimpleBufferPool(1048576) ' 1 MB

End Module