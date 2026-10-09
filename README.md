# Dhole Dynamics Connector Service

Minimal API (.NET 10) connecting Dhole with Microsoft Dynamics 365 / Dataverse.

Features:
- Create Dataverse Quotes from Dhole Pricing, with idempotency via local PostgreSQL links.
- Read Quotes by Dataverse ID.
- Manage Dynamics connection metadata and **AES-256-GCM encrypted** application secrets in PostgreSQL.
- Validate Dhole JWT permissions before reading or modifying connections and quotes.
- Docker image, SQL schema, deployment guide, and automated tests.

> **Security**: No keys, passwords or tokens belong in Git. The encryption master key is provided by a server secret/env variable, separate from the database. Rotate any legacy secrets previously committed to DynaConnect. This repository is currently public; change visibility to private before working with proprietary configuration.

See `docs/DEPLOYMENT.md` for setup details.