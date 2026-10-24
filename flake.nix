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
