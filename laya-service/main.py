import os
import logging
from typing import Dict, Any, Optional, List, Union
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("laya-service")

app = FastAPI(title="Laya Offline Decision Engine", version="1.0.0")

# Singleton router instance
router_instance = None

def get_router():
    global router_instance
    if router_instance is None:
        try:
            from laya import Router
            logger.info("Initializing Laya Router with preloaded local checkpoints...")
            # Router(preload=True) loads the preloaded checkpoints from HF_HOME
            router_instance = Router(preload=True)
            logger.info("Laya Router loaded successfully into memory.")
        except Exception as ex:
            logger.error(f"Error initializing Laya Router: {ex}")
            raise
    return router_instance

@app.on_event("startup")
def on_startup():
    try:
        get_router()
    except Exception as ex:
        logger.warning(f"Laya model deferred or warmup failed: {ex}")

class AskRequest(BaseModel):
    context: str
    question: str
    type: str  # "choice" | "noul" | "score"
    choices: Optional[Union[List[str], Dict[str, str]]] = None

class AskResponse(BaseModel):
    answer: Any
    probability: float
    raw: Dict[str, Any]
    routing: Dict[str, Any]

class PredictRequest(BaseModel):
    state: str
    questions: Dict[str, Any]
    model: Optional[str] = None
    max_len: Optional[int] = None

@app.get("/health")
def health():
    return {
        "status": "ready" if router_instance is not None else "starting",
        "engine": "laya",
        "offline": True
    }

@app.post("/predict")
def predict(req: PredictRequest):
    r = get_router()
    try:
        kwargs: Dict[str, Any] = {}
        if req.model:
            kwargs["model"] = req.model
        if req.max_len:
            kwargs["max_len"] = req.max_len
        result = r.predict(req.state, req.questions, **kwargs)
        return result
    except Exception as ex:
        logger.error(f"Prediction error: {ex}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(ex))

@app.post("/ask", response_model=AskResponse)
def ask(req: AskRequest):
    r = get_router()
    try:
        q_dict: Dict[str, Any] = {
            "type": req.type,
            "instructions": req.question
        }

        if req.type == "choice" and req.choices is not None:
            if isinstance(req.choices, list):
                q_dict["criteria"] = {c: c for c in req.choices}
            elif isinstance(req.choices, dict):
                q_dict["criteria"] = req.choices

        pred = r.predict(req.context, {"q": q_dict})
        q_ans = pred.get("answers", {}).get("q", {})
        routing = pred.get("routing", {})

        answer = None
        probability = 1.0

        if req.type == "choice":
            answer = q_ans.get("choice")
            # If probabilities dict available, take selected choice's prob
            probs = q_ans.get("probabilities", {})
            if isinstance(probs, dict) and answer in probs:
                probability = float(probs[answer])
            elif "confidence" in q_ans:
                probability = float(q_ans["confidence"])
        elif req.type == "noul":
            val = q_ans.get("noul", 0.0)
            probability = float(val)
            answer = bool(probability >= 0.5)
        elif req.type == "score":
            answer = q_ans.get("score")
            if "confidence" in q_ans:
                probability = float(q_ans["confidence"])

        return AskResponse(
            answer=answer,
            probability=probability,
            raw=q_ans,
            routing=routing
        )
    except Exception as ex:
        logger.error(f"Ask error: {ex}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(ex))
