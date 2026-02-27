namespace SmallFile.Core;

public enum EngineState
{
    Idle,
    TcpConnected,
    HandshakingCrypto,
    AwaitingSas,
    SessionSecured,
    Terminated
}