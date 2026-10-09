CREATE TABLE IF NOT EXISTS accounts
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username            VARCHAR(32) NOT NULL,
    normalized_username VARCHAR(32) NOT NULL UNIQUE,
    password_hash       BYTEA NOT NULL,
    password_salt       BYTEA NOT NULL,
    password_iterations INTEGER NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);