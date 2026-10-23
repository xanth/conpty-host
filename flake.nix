{
  description = "ConPTY terminal backend host";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
      in
      {
        devShells.default = pkgs.mkShell {
          name = "conpty-host-dev";

          buildInputs = with pkgs; [
            # NOTE: nixpkgs tracks released SDKs. The project pins .NET 11 in
            # global.json, which is RC until November 2026, so this shell
            # currently provides .NET 10 and `dotnet build` will refuse until
            # an 11.x SDK is available. See OPEN-QUESTIONS.md.
            dotnet-sdk_10
            powershell
            git
          ];

          shellHook = ''
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_NOLOGO=1
            echo "conpty-host dev shell - dotnet $(dotnet --version 2>/dev/null || echo 'unavailable')"
          '';
        };
      });
}
