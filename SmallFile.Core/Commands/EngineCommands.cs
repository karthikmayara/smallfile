using System.Collections.Generic;
using SmallFile.Core.Models;

namespace SmallFile.Core.Commands;

internal sealed record StartConnectionCommand() : IEngineCommand;
internal sealed record TransportConnectedCommand() : IEngineCommand;
internal sealed record TransportDisconnectedCommand() : IEngineCommand;
internal sealed record NetworkFrameReceivedCommand(byte[] Payload) : IEngineCommand;
internal sealed record ConfirmSasCommand(bool Accepted) : IEngineCommand;

// Phase 1: Metadata Exchange
internal sealed record RequestTreeCommand() : IEngineCommand;
internal sealed record SendTreeCommand(List<FileEntry> Files) : IEngineCommand;

// Phase 1: File Transfer
internal sealed record RequestFileCommand(string RelativePath) : IEngineCommand;
internal sealed record SendFileChunkCommand(string RelativePath, long Offset, byte[] Data) : IEngineCommand;
internal sealed record SendFileCompleteCommand(string RelativePath) : IEngineCommand;