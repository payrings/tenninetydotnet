# Local model weights (not committed)

This directory is the default location for the two GGUF weight files that the
llama-swap container serves. Everything here except this README is ignored by
git - **GGUF files must never be committed**.

| File | Consumed by | Default path in `docker/llama-swap.yaml` |
| --- | --- | --- |
| `coder.gguf` | the Coder (aider / OpenCode / Pi) | `/models/coder.gguf` |
| `reviewer.gguf` | the Reviewer | `/models/reviewer.gguf` |

## Placement

1. Download the two GGUF files yourself (separately, before starting the
   model server), for example with the Hugging Face CLI:

   ```fish
   uvx --from "huggingface_hub[cli]" hf download <org>/<repo> <file>.gguf \
       --local-dir ~/Models
   ```

2. Copy or symlink them into this directory with the expected names, or point
   `TENNINETY_MODELS_DIR` in `.env` at the directory that already holds them:

   ```fish
   cp ~/Models/Qwen3.6-27B-Q4_K_M.gguf ./models/coder.gguf
   cp ~/Models/Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf ./models/reviewer.gguf
   ```

   (Filenames above are examples - pick genuinely different coder and reviewer
   weights that fit your 24 GB card; the framework only enforces that the two
   configured *identifiers* differ.)

3. Record checksums so silent weight drift is detectable:

   ```fish
   sha256sum ./models/coder.gguf ./models/reviewer.gguf > MODELS.sha256
   sha256sum --check MODELS.sha256
   ```

   `MODELS.sha256` is also git-ignored; keep it outside the repository or
   `git add -f` it deliberately.

## Different names

To serve differently named files, edit the `coder_model` / `reviewer_model`
macros at the top of `docker/llama-swap.yaml` - the `cmd:` lines reference
them, so nothing else changes. llama-swap applies config edits without a
container restart.
