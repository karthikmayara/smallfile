namespace SmallFile.Core;

internal enum EngineState
{
    Idle,
    TcpConnected,
    HandshakingCrypto,
    AwaitingSas,
    SessionSecured,
    Terminated
}