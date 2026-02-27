namespace SmallFile.Core.Commands;

internal sealed record StartConnectionCommand() : IEngineCommand;

internal sealed record TransportConnectedCommand() : IEngineCommand;

internal sealed record TransportDisconnectedCommand() : IEngineCommand;

internal sealed record NetworkFrameReceivedCommand(byte[] Payload) : IEngineCommand;

internal sealed record ConfirmSasCommand(bool Accepted) : IEngineCommand;