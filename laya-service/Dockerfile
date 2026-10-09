FROM python:3.11-slim

WORKDIR /app

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    HF_HOME=/models/cache

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install PyTorch CPU first to keep image lightweight (~800MB vs 4GB CUDA)
RUN pip install --no-cache-dir torch --extra-index-url https://download.pytorch.org/whl/cpu

# Install FastAPI and Laya
RUN pip install --no-cache-dir \
    fastapi==0.115.6 \
    uvicorn[standard]==0.34.0 \
    pydantic==2.10.4 \
    "transformers>=4.48.0" \
    huggingface_hub>=0.27.0 \
    laya

# Pre-download and bake model weights permanently inside image during docker build
# This guarantees 100% offline edge execution with zero internet connectivity at runtime
RUN HF_HUB_OFFLINE=0 TRANSFORMERS_OFFLINE=0 python -c "from laya import Router; Router(preload=True)"

# Set offline runtime flags so no network lookups occur
ENV HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1

COPY main.py .

EXPOSE 8001

HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=5 \
  CMD curl -f http://localhost:8001/health || exit 1

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8001"]
