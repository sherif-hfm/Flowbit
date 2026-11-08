# Local webhook key-ring certificate

The Compose stack mounts this folder read-only into the API and Worker. Put `webhook-keys.pfx` here to use lifecycle webhooks; see [signing keys and the key ring](../../docs/deployment.md#signing-keys-and-the-key-ring). Git ignores `.pfx` files, and Docker image builds never include this folder.
