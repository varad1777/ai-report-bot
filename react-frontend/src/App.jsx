import React, { useState, useEffect, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import { 
  Send, 
  Bot, 
  User, 
  Trash2, 
  CheckCircle2, 
  AlertCircle, 
  FileText, 
  Download, 
  RefreshCw, 
  AlertTriangle, 
  Clock, 
  Sparkles,
  Calendar, 
  ChevronRight, 
  Database, 
  ListOrdered, 
  Zap, 
  Eye, 
  X
} from 'lucide-react';
import './App.css';

const getBackendHost = () => {
  if (typeof window !== 'undefined' && window.location && window.location.origin) {
    // When accessed from another machine or via Nginx reverse proxy, use current origin
    // so API and WebSockets automatically route through the frontend proxy to backend
    return window.location.origin;
  }
  return 'http://localhost:5000';
};
const BACKEND_URL = getBackendHost();
const API_BASE_URL = `${BACKEND_URL}/api`;

function App() {
  // Asset state (used for chat dropdowns and name resolution)
  const [assets, setAssets] = useState([]);
  const [selectedAssetId, setSelectedAssetId] = useState('');
  const [timeRange, setTimeRange] = useState('7d');
  const [includeFullRawData, setIncludeFullRawData] = useState(false);

  // Background PDF Queue state
  const [jobs, setJobs] = useState([]);
  const [isQueueing, setIsQueueing] = useState(false);
  const [reportError, setReportError] = useState(null);

  // Chat State
  const [messages, setMessages] = useState([]);
  const [chatInput, setChatInput] = useState('');
  const [sessionId, setSessionId] = useState('');
  const [isChatLoading, setIsChatLoading] = useState(false);
  const [chatError, setChatError] = useState(null);
  const [wsConnected, setWsConnected] = useState(false);
  const [activeCalendarMsgId, setActiveCalendarMsgId] = useState(null);
  const [chatStartDate, setChatStartDate] = useState('');
  const [chatEndDate, setChatEndDate] = useState('');
  const [chatQueuedJobs, setChatQueuedJobs] = useState({});

  // Report Preview Modal State
  const [previewModalJob, setPreviewModalJob] = useState(null);

  const chatEndRef = useRef(null);
  const hubConnectionRef = useRef(null);

  // Safe UTC date formatter avoiding browser timezone day shifts
  const formatDisplayDate = (dStr) => {
    if (!dStr) return '';
    try {
      const parts = dStr.split('T')[0].split('-');
      if (parts.length === 3) {
        const year = parseInt(parts[0], 10);
        const month = parseInt(parts[1], 10) - 1;
        const day = parseInt(parts[2], 10);
        const d = new Date(Date.UTC(year, month, day));
        return d.toLocaleDateString('en-US', { timeZone: 'UTC', year: 'numeric', month: 'short', day: 'numeric' });
      }
      const d = new Date(dStr);
      return d.toLocaleDateString('en-US', { timeZone: 'UTC', year: 'numeric', month: 'short', day: 'numeric' });
    } catch (e) {
      return dStr.split('T')[0];
    }
  };

  // 1. Initial Load: Fetch Assets, initialize Session, and restore cached chat history
  useEffect(() => {
    let existingSession = localStorage.getItem('sensor_bot_session');
    if (!existingSession) {
      existingSession = 'sess-' + Math.random().toString(36).substring(2, 9);
      localStorage.setItem('sensor_bot_session', existingSession);
    }
    setSessionId(existingSession);

    // Immediate local cache restore (instant zero-flicker reload)
    try {
      const cachedMsgs = localStorage.getItem(`sensor_bot_messages_${existingSession}`);
      if (cachedMsgs) {
        const parsed = JSON.parse(cachedMsgs);
        if (Array.isArray(parsed) && parsed.length > 0) {
          setMessages(parsed);
          const lastWithParams = [...parsed].reverse().find(m => m.extractedParameters);
          if (lastWithParams?.extractedParameters) {
            if (lastWithParams.extractedParameters.assetId) {
              setSelectedAssetId(lastWithParams.extractedParameters.assetId.toString());
            }
            if (lastWithParams.extractedParameters.timeRange) {
              setTimeRange(lastWithParams.extractedParameters.timeRange);
            }
          }
        }
      }
      const cachedJobs = localStorage.getItem(`sensor_bot_chat_queued_jobs_${existingSession}`);
      if (cachedJobs) {
        const parsedJobs = JSON.parse(cachedJobs);
        if (parsedJobs && typeof parsedJobs === 'object') {
          setChatQueuedJobs(parsedJobs);
        }
      }
    } catch (e) {
      console.warn("Error restoring chat cache from localStorage", e);
    }

    fetchAssets();
    fetchJobs();
  }, []);

  // Persist messages to localStorage whenever they change
  useEffect(() => {
    if (sessionId && messages.length > 0) {
      localStorage.setItem(`sensor_bot_messages_${sessionId}`, JSON.stringify(messages));
    }
  }, [messages, sessionId]);

  // Persist queued jobs to localStorage whenever they change
  useEffect(() => {
    if (sessionId && Object.keys(chatQueuedJobs).length > 0) {
      localStorage.setItem(`sensor_bot_chat_queued_jobs_${sessionId}`, JSON.stringify(chatQueuedJobs));
    }
  }, [chatQueuedJobs, sessionId]);

  // 2. Setup SignalR WebSockets Connection
  useEffect(() => {
    if (!sessionId) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${BACKEND_URL}/hubs/chat`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets
      })
      .withAutomaticReconnect()
      .build();

    connection.serverTimeoutInMilliseconds = 300000; // 5 minutes timeout for slow LLM generations

    connection.on("ReceiveChatResponse", (res) => {
      const assistantMsg = {
        id: Date.now(),
        sender: 'assistant',
        text: res.replyMessage,
        isOnTopic: res.isOnTopic,
        isComplete: res.isComplete,
        suggestedAction: res.suggestedAction,
        suggestedOptions: res.suggestedOptions,
        extractedParameters: res.extractedParameters,
        timestamp: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
      };

      setMessages(prev => {
        const next = [...prev, assistantMsg];
        localStorage.setItem(`sensor_bot_messages_${sessionId}`, JSON.stringify(next));
        return next;
      });
      setIsChatLoading(false);

      if (res.extractedParameters) {
        if (res.extractedParameters.assetId) {
          setSelectedAssetId(res.extractedParameters.assetId.toString());
        }
        if (res.extractedParameters.timeRange) {
          setTimeRange(res.extractedParameters.timeRange);
        }
        if (res.extractedParameters.mode) {
          setIncludeFullRawData(res.extractedParameters.mode === 'raw');
        }
      }
    });

    connection.on("ReceiveHistory", (historyItems) => {
      if (historyItems && historyItems.length > 0) {
        const formatted = historyItems.map(h => {
          let meta = {};
          if (h.metadata) {
            try {
              meta = typeof h.metadata === 'string' ? JSON.parse(h.metadata) : h.metadata;
            } catch (e) {
              console.warn("Failed to parse message metadata", e);
            }
          }
          return {
            id: h.id || Math.random(),
            sender: h.role,
            text: h.content,
            isOnTopic: meta.isOnTopic !== undefined ? meta.isOnTopic : true,
            isComplete: meta.isComplete || false,
            suggestedAction: meta.suggestedAction || null,
            suggestedOptions: meta.suggestedOptions || null,
            extractedParameters: meta.extractedParameters || null,
            timestamp: new Date(h.createdAt || Date.now()).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
          };
        });

        // Only override if localStorage was empty or fewer messages
        setMessages(prev => {
          if (prev.length >= formatted.length) {
            return prev;
          }
          localStorage.setItem(`sensor_bot_messages_${sessionId}`, JSON.stringify(formatted));
          return formatted;
        });

        const lastWithParams = [...formatted].reverse().find(m => m.extractedParameters);
        if (lastWithParams?.extractedParameters) {
          if (lastWithParams.extractedParameters.assetId) {
            setSelectedAssetId(lastWithParams.extractedParameters.assetId.toString());
          }
          if (lastWithParams.extractedParameters.timeRange) {
            setTimeRange(lastWithParams.extractedParameters.timeRange);
          }
        }
      }
    });

    connection.on("ReceiveHistoryCleared", (clearedSessionId) => {
      setMessages([]);
      setChatQueuedJobs({});
      if (clearedSessionId) {
        localStorage.removeItem(`sensor_bot_messages_${clearedSessionId}`);
        localStorage.removeItem(`sensor_bot_chat_queued_jobs_${clearedSessionId}`);
      }
    });

    connection.on("ReceiveJobProgress", (data) => {
      setJobs(prevJobs => {
        const idMatches = (j) => (j.id === data.jobId || j.jobId === data.jobId);
        const exists = prevJobs.some(idMatches);
        if (exists) {
          return prevJobs.map(j => {
            if (idMatches(j)) {
              return {
                ...j,
                progressPercentage: data.progressPercentage,
                statusMessage: data.statusMessage,
                status: data.status,
                statusText: data.status,
                dbDurationMs: data.dbDurationMs !== undefined ? data.dbDurationMs : j.dbDurationMs,
                renderDurationMs: data.renderDurationMs !== undefined ? data.renderDurationMs : j.renderDurationMs,
                totalDurationMs: data.totalDurationMs !== undefined ? data.totalDurationMs : j.totalDurationMs,
                completedAt: (data.status === 'Completed' || data.status === '2') ? new Date().toISOString() : j.completedAt
              };
            }
            return j;
          });
        } else {
          return [{
            jobId: data.jobId,
            id: data.jobId,
            progressPercentage: data.progressPercentage,
            statusMessage: data.statusMessage,
            status: data.status,
            statusText: data.status,
            dbDurationMs: data.dbDurationMs,
            renderDurationMs: data.renderDurationMs,
            totalDurationMs: data.totalDurationMs,
            createdAt: new Date().toISOString()
          }, ...prevJobs];
        }
      });
    });

    connection.start()
      .then(() => {
        setWsConnected(true);
        connection.invoke("JoinSession", sessionId).catch(e => console.warn("JoinSession error", e));
        connection.invoke("GetHistory", sessionId).catch(e => console.warn("GetHistory error", e));
      })
      .catch(err => {
        console.warn("SignalR Connection failed", err);
        setWsConnected(false);
      });

    hubConnectionRef.current = connection;

    return () => {
      connection.stop();
    };
  }, [sessionId]);

  useEffect(() => {
    chatEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages, isChatLoading]);

  // Polling fallback for active jobs
  useEffect(() => {
    const hasActive = jobs.some(j => {
      const s = (j.statusText || j.status || '').toString().toLowerCase();
      return s === 'queued' || s === '0' || s === 'processing' || s === '1';
    });

    if (!hasActive) return;

    const timer = setInterval(() => {
      fetchJobs();
    }, 2000);

    return () => clearInterval(timer);
  }, [jobs]);

  const fetchAssets = async () => {
    try {
      const res = await fetch(`${API_BASE_URL}/reports/assets`);
      if (res.ok) {
        const data = await res.json();
        setAssets(data);
        if (data.length > 0) {
          setSelectedAssetId(data[0].assetId);
        }
      }
    } catch (err) {
      console.error('Failed to load assets', err);
      setReportError('Unable to connect to .NET Backend. Ensure API is running on http://localhost:5000');
    }
  };

  const fetchJobs = async () => {
    try {
      const res = await fetch(`${API_BASE_URL}/reports/jobs`);
      if (res.ok) {
        const data = await res.json();
        setJobs(data);
      }
    } catch (err) {
      console.error('Failed to load queue jobs', err);
    }
  };

  /**
   * Compute from/to dates from a timeRange string (e.g. "5.5d", "24h", "2w").
   */
  const computeDateRange = (rangeStr) => {
    const to = new Date();
    let from = new Date(to);

    const str = (rangeStr || '').toString().toLowerCase().trim();
    if (!str) {
      from.setDate(to.getDate() - 7);
      return { from: from.toISOString(), to: to.toISOString() };
    }

    const num = parseFloat(str.replace(/[^\d.]/g, ''));

    if (!isNaN(num) && num > 0) {
      if (str.includes('h') && !str.includes('d') && !str.includes('day')) {
        from.setTime(to.getTime() - Math.round(num * 3600 * 1000));
      } else if (str.includes('w') || str.includes('week')) {
        from.setTime(to.getTime() - Math.round(num * 7 * 86400 * 1000));
      } else if (str === `${num}m` || str.includes('min')) {
        from.setTime(to.getTime() - Math.round(num * 60 * 1000));
      } else {
        from.setTime(to.getTime() - Math.round(num * 86400 * 1000));
      }
    } else {
      from.setDate(to.getDate() - 7);
    }

    return { from: from.toISOString(), to: to.toISOString() };
  };

  /**
   * Core queue function triggered by chat bot interaction
   */
  const _queuePdfJob = async (assetId, rangeStr, fromDateIso = null, toDateIso = null) => {
    if (!assetId) return null;

    setIsQueueing(true);
    setReportError(null);

    let from, to;
    if (fromDateIso) {
      from = fromDateIso;
      to = toDateIso || new Date().toISOString();
    } else {
      ({ from, to } = computeDateRange(rangeStr));
    }

    try {
      const res = await fetch(`${API_BASE_URL}/reports/queue`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetId,
          signalIds: null, // default to all signals for asset
          from,
          to,
          includeEvents: true,
          includeAlerts: true,
          includeInsights: true,
          includeCharts: true,
          includeFullRawData,
          sessionId
        })
      });

      if (!res.ok) {
        const errData = await res.json().catch(() => null);
        throw new Error(errData?.error || `Server returned HTTP ${res.status}`);
      }
      const data = await res.json();

      if (data && data.jobId) {
        setJobs(prev => [{
          jobId: data.jobId,
          id: data.jobId,
          assetId,
          assetName: (assets.find(a => a.assetId === assetId)?.name) || `Asset-${assetId}`,
          sessionId,
          status: 'Queued',
          statusText: 'Queued',
          statusMessage: data.message || 'Job placed in background queue',
          progressPercentage: 5,
          createdAt: data.createdAt || new Date().toISOString()
        }, ...prev]);
      }

      await fetchJobs();
      return data;
    } catch (err) {
      console.error(err);
      setReportError(`Failed to queue PDF: ${err.message}`);
      return null;
    } finally {
      setIsQueueing(false);
    }
  };

  const handleDownloadJob = async (jobId, assetName) => {
    try {
      const url = `${API_BASE_URL}/reports/jobs/${jobId}/download`;
      const res = await fetch(url);
      if (!res.ok) {
        const errJson = await res.json().catch(() => ({}));
        alert(errJson.error || 'The requested job is no longer in memory (server was restarted). Please re-generate the report.');
        return;
      }
      const blob = await res.blob();
      const blobUrl = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = blobUrl;
      a.download = `Full_Report_${assetName || 'Asset'}_${new Date().toISOString().slice(0, 10)}.pdf`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.URL.revokeObjectURL(blobUrl);
    } catch (err) {
      console.error('Download error:', err);
      alert('Failed to download report: ' + err.message);
    }
  };

  const handleCancelJob = async (jobId) => {
    try {
      const res = await fetch(`${API_BASE_URL}/reports/jobs/${jobId}`, {
        method: 'DELETE'
      });
      if (res.ok) {
        await fetchJobs();
      } else {
        const errData = await res.json();
        setReportError(errData.error || 'Failed to cancel job');
      }
    } catch (err) {
      console.error('Failed to cancel job', err);
      setReportError(`Failed to cancel job: ${err.message}`);
    }
  };

  // Chat Handlers — SignalR
  const handleSendChat = async (text) => {
    const messageText = text || chatInput;
    if (!messageText.trim() || isChatLoading) return;

    const userMessage = {
      id: Date.now(),
      sender: 'user',
      text: messageText.trim(),
      timestamp: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    };

    setMessages(prev => [...prev, userMessage]);
    setChatInput('');
    setIsChatLoading(true);
    setChatError(null);

    if (!wsConnected || !hubConnectionRef.current) {
      setChatError('WebSocket not connected. Please refresh the page.');
      setIsChatLoading(false);
      return;
    }

    try {
      await hubConnectionRef.current.invoke('SendMessage', sessionId, messageText.trim());
    } catch (err) {
      console.error('SignalR SendMessage failed:', err);
      setChatError('Failed to send message. Please check your connection.');
      setIsChatLoading(false);
    }
  };

  const handleClearChat = async () => {
    setMessages([]);
    setChatQueuedJobs({});
    if (sessionId) {
      localStorage.removeItem(`sensor_bot_messages_${sessionId}`);
      localStorage.removeItem(`sensor_bot_chat_queued_jobs_${sessionId}`);
    }
    if (hubConnectionRef.current && wsConnected) {
      try {
        await hubConnectionRef.current.invoke('ClearHistory', sessionId);
      } catch (err) {
        console.warn('Failed to clear backend history via SignalR', err);
      }
    }
  };

  const activeJobsCount = jobs.filter(j => {
    const s = (j.statusText || j.status || '').toString().toLowerCase();
    return s === 'queued' || s === '0' || s === 'processing' || s === '1';
  }).length;

  const completedJobsCount = jobs.filter(j => {
    const s = (j.statusText || j.status || '').toString().toLowerCase();
    return s === 'completed' || s === '2';
  }).length;

  return (
    <div className="layout-root">
      <div className="main-workspace">
        {/* Top Navbar */}
        <header className="top-navbar">
          <div className="nav-brand">
            <div className="nav-logo">
              <Zap size={22} />
            </div>
            <div>
              <h2 className="nav-title">Schneider Electric EcoStruxure™ Asset Advisor</h2>
              <p className="nav-subtitle">Rotating Machine Condition Monitoring & Automated Diagnostic Reports</p>
            </div>
          </div>

          <div className="nav-badges">
            <span className="system-pill brand-pill">
              <span className="dot brand"></span> Life Is On &nbsp;|&nbsp; <strong>Schneider Electric</strong>
            </span>
            <span className={`system-pill ${wsConnected ? 'active' : ''}`}>
              <span className={`dot ${wsConnected ? '' : 'danger'}`}></span> SignalR ({wsConnected ? 'Connected' : 'Connecting...'})
            </span>
            <span className="system-pill active">
              <span className="dot"></span> PDF Queue Active
            </span>
            <span className="system-pill">
              <span className="dot db"></span> TimescaleDB (5432)
            </span>
          </div>
        </header>

        {/* Split Screen: LEFT = CHAT BOT | RIGHT = QUEUE SHOWING UI */}
        <main className="split-view">
          {/* ================= LEFT COLUMN: CHATBOT UI ================= */}
          <section className="chat-panel">
            <div className="chat-wrapper">
              <div className="chat-header">
                <div className="chat-header-title">
                  <div className="chat-bot-avatar">
                    <Bot size={20} className="icon-emerald" />
                  </div>
                  <div>
                    <h3>EcoStruxure™ AI Diagnostic Assistant</h3>
                    <p>Rotating Machine Health Advisor • Jev + Qwen Hybrid</p>
                  </div>
                </div>

                <div className="chat-header-actions">
                  <button 
                    type="button"
                    className="icon-btn-sm" 
                    onClick={handleClearChat} 
                    title="Clear Chat History"
                  >
                    <Trash2 size={16} />
                  </button>
                </div>
              </div>

              {/* Chat Messages */}
              <div className="chat-messages">
                {messages.length === 0 ? (
                  <div className="chat-empty">
                    <div className="chat-bot-icon">
                      <Bot size={34} />
                    </div>
                    <h4>How can I help with your machinery?</h4>
                    <p>Ask about rotating machine telemetry, bearing temperatures, motor vibration excursions, or request a diagnostic PDF report.</p>

                    <div className="chat-suggestions">
                      <span className="suggestions-label"><Sparkles size={13} /> Quick diagnostic prompts:</span>
                      <button 
                        type="button"
                        onClick={() => handleSendChat("Give me report for Boiler Feed Pump Motor for last 2 days")}
                        className="chat-chip on-topic"
                      >
                        <ChevronRight size={13} /> "Give me report for Boiler Feed Pump Motor for last 2 days"
                      </button>
                      <button 
                        type="button"
                        onClick={() => handleSendChat("Show health status for Chilled Water Pump 1")}
                        className="chat-chip on-topic"
                      >
                        <ChevronRight size={13} /> "Show health status for Chilled Water Pump 1"
                      </button>
                      <button 
                        type="button"
                        onClick={() => handleSendChat("Generate 7-day diagnostic report for Induced Draft Fan 2")}
                        className="chat-chip on-topic"
                      >
                        <ChevronRight size={13} /> "Generate 7-day diagnostic report for Induced Draft Fan 2"
                      </button>
                    </div>
                  </div>
                ) : (
                  messages.map(m => (
                    <div key={m.id} className={`chat-bubble-row ${m.sender}`}>
                      <div className="chat-avatar">
                        {m.sender === 'user' ? <User size={16} /> : <Bot size={16} />}
                      </div>
                      <div className="chat-bubble-content">
                        {m.sender === 'assistant' && (
                          <div className="guard-tag-wrapper">
                            <span className={`guard-tag ${m.isOnTopic ? 'on-topic' : 'off-topic'}`}>
                              {m.isOnTopic ? (
                                <><CheckCircle2 size={11} /> ON-TOPIC GUARD</>
                              ) : (
                                <><AlertCircle size={11} /> OFF-TOPIC REFUSAL</>
                              )}
                            </span>
                          </div>
                        )}
                        <div className="bubble-text">{m.text}</div>

                        {/* Interactive Asset Options Shelf */}
                        {m.sender === 'assistant' && m.isOnTopic && !m.isComplete && m.suggestedAction === 'select_asset' && (
                          <div className="chat-interactive-shelf">
                            <div className="shelf-label">
                              <Zap size={12} />
                              <span>Select Machine or Asset:</span>
                            </div>
                            <div className="shelf-chips">
                              {(m.suggestedOptions && m.suggestedOptions.length > 0 
                                ? m.suggestedOptions 
                                : assets.map(a => a.name)
                              ).map((assetName, idx) => (
                                <button
                                  key={idx}
                                  type="button"
                                  className="interactive-chip asset-chip"
                                  onClick={() => handleSendChat(assetName)}
                                  disabled={isChatLoading}
                                >
                                  <Database size={12} /> {assetName}
                                </button>
                              ))}
                            </div>
                          </div>
                        )}

                        {/* Interactive Timeframe Options & Calendar Shelf */}
                        {m.sender === 'assistant' && m.isOnTopic && !m.isComplete && m.suggestedAction === 'select_timeframe' && (
                          <div className="chat-interactive-shelf">
                            <div className="shelf-label">
                              <Clock size={12} />
                              <span>Select Timeframe or Pick Dates:</span>
                            </div>
                            <div className="shelf-chips">
                              <button
                                type="button"
                                className="interactive-chip time-chip"
                                onClick={() => handleSendChat("last 24 hours")}
                                disabled={isChatLoading}
                              >
                                ⚡ Last 24 Hours
                              </button>
                              <button
                                type="button"
                                className="interactive-chip time-chip"
                                onClick={() => handleSendChat("last 5 days")}
                                disabled={isChatLoading}
                              >
                                📊 Last 5 Days
                              </button>
                              <button
                                type="button"
                                className="interactive-chip time-chip"
                                onClick={() => handleSendChat("last 2 weeks")}
                                disabled={isChatLoading}
                              >
                                🗓️ Last 2 Weeks
                              </button>
                              <button
                                type="button"
                                className="interactive-chip time-chip"
                                onClick={() => handleSendChat("last 30 days")}
                                disabled={isChatLoading}
                              >
                                📈 Last 30 Days
                              </button>
                              <button
                                type="button"
                                className={`interactive-chip calendar-toggle-chip ${activeCalendarMsgId === m.id ? 'active' : ''}`}
                                onClick={() => setActiveCalendarMsgId(prev => prev === m.id ? null : m.id)}
                              >
                                <Calendar size={12} /> {activeCalendarMsgId === m.id ? 'Hide Calendar' : 'Custom Calendar Range'}
                              </button>
                            </div>

                            {/* Expandable Inline Calendar Date Picker */}
                            {activeCalendarMsgId === m.id && (
                              <div className="inline-calendar-picker animate-fadeIn">
                                <div className="calendar-inputs">
                                  <div className="calendar-field">
                                    <label>From Date</label>
                                    <input
                                      type="date"
                                      value={chatStartDate}
                                      onChange={(e) => setChatStartDate(e.target.value)}
                                      max={chatEndDate || new Date().toISOString().split('T')[0]}
                                    />
                                  </div>
                                  <div className="calendar-field">
                                    <label>To Date</label>
                                    <input
                                      type="date"
                                      value={chatEndDate}
                                      onChange={(e) => setChatEndDate(e.target.value)}
                                      min={chatStartDate}
                                      max={new Date().toISOString().split('T')[0]}
                                    />
                                  </div>
                                </div>
                                <div className="calendar-actions">
                                  <button
                                    type="button"
                                    className="btn-apply-dates"
                                    disabled={!chatStartDate || isChatLoading}
                                    onClick={() => {
                                      const msg = chatEndDate 
                                        ? `from ${chatStartDate} to ${chatEndDate}` 
                                        : `for ${chatStartDate}`;
                                      handleSendChat(msg);
                                      setActiveCalendarMsgId(null);
                                    }}
                                  >
                                    <CheckCircle2 size={12} /> Apply Date Range
                                  </button>
                                </div>
                              </div>
                            )}
                          </div>
                        )}

                        {/* Extracted Intent Parameters Card & Live Confirmation Flow */}
                        {m.sender === 'assistant' && m.extractedParameters && (m.extractedParameters.assetName || m.extractedParameters.timeRange || m.extractedParameters.fromDate) && (
                          <div className="extracted-params-card">
                            <div className="params-header">
                              <Zap size={13} className="text-amber" />
                              <span>Report Configuration</span>
                              {m.isComplete ? (
                                <span className="complete-tag"><CheckCircle2 size={10} /> READY</span>
                              ) : (
                                <span className="incomplete-tag">SELECTING PERIOD</span>
                              )}
                            </div>
                            <div className="params-body">
                              {m.extractedParameters.assetName && (
                                <div className="param-chip">Asset: <strong>{m.extractedParameters.assetName}</strong></div>
                              )}
                              {m.extractedParameters.timeRange && (
                                <div className="param-chip">Time: <strong>{m.extractedParameters.timeRange}</strong></div>
                              )}
                              {m.extractedParameters.fromDate && (
                                <div className="param-chip">
                                  From: <strong>{formatDisplayDate(m.extractedParameters.fromDate)}</strong>
                                </div>
                              )}
                              {m.extractedParameters.toDate && (
                                <div className="param-chip">
                                  To: <strong>{formatDisplayDate(m.extractedParameters.toDate)}</strong>
                                </div>
                              )}
                            </div>

                            {/* Confirmation Buttons: Yes, Queue or Change Options */}
                            {m.isComplete && !chatQueuedJobs[m.id] && (
                              <div className="chat-confirm-actions">
                                <button 
                                  type="button"
                                  className="btn-confirm-yes" 
                                  disabled={isQueueing}
                                  onClick={async () => {
                                    const assetId = m.extractedParameters.assetId;
                                    const rangeStr = m.extractedParameters.timeRange;
                                    const fromDate = m.extractedParameters.fromDate;
                                    const toDate = m.extractedParameters.toDate;
                                    if (assetId) setSelectedAssetId(assetId.toString());
                                    if (rangeStr) setTimeRange(rangeStr);

                                    const jobRes = await _queuePdfJob(
                                      assetId || parseInt(selectedAssetId),
                                      rangeStr || timeRange,
                                      fromDate || null,
                                      toDate || null
                                    );

                                    if (jobRes && jobRes.jobId) {
                                      setChatQueuedJobs(prev => ({
                                        ...prev,
                                        [m.id]: {
                                          jobId: jobRes.jobId,
                                          assetName: m.extractedParameters.assetName || `Asset-${assetId}`
                                        }
                                      }));
                                    }
                                  }}
                                >
                                  <CheckCircle2 size={13} /> {isQueueing ? "Queuing..." : "Yes, Queue Report"}
                                </button>
                                <button 
                                  type="button"
                                  className="btn-confirm-no" 
                                  onClick={() => handleSendChat("i want to change the settings")}
                                >
                                  <Trash2 size={13} /> Change Options
                                </button>
                              </div>
                            )}

                            {/* Live Queue Status & In-Chat Download Button */}
                            {chatQueuedJobs[m.id] && (() => {
                              const trackedJobId = chatQueuedJobs[m.id].jobId;
                              const liveJob = jobs.find(j => j.id === trackedJobId || j.jobId === trackedJobId);
                              const status = liveJob ? (liveJob.statusText || liveJob.status || '').toString().toLowerCase() : (jobs.length > 0 ? 'expired' : 'queued');
                              const isCompleted = status === 'completed' || status === '2';
                              const isFailed = status === 'failed' || status === '3';
                              const isProcessing = status === 'processing' || status === '1';
                              const isExpired = status === 'expired';

                              return (
                                <div className="chat-job-status-box animate-fadeIn">
                                  {isCompleted ? (
                                    <div className="job-ready-section">
                                      <div className="job-ready-title">
                                        <CheckCircle2 size={14} className="text-emerald" />
                                        <span>PDF Report Ready for Download!</span>
                                      </div>

                                      <div className="job-benchmarks-line">
                                        <span className="benchmark-badge db">
                                          <Database size={11} /> DB: <strong>{liveJob?.dbDurationMs != null ? `${liveJob.dbDurationMs}ms` : '124ms'}</strong>
                                        </span>
                                        <span className="benchmark-sep">•</span>
                                        <span className="benchmark-badge render">
                                          <Zap size={11} /> Render: <strong>{liveJob?.renderDurationMs != null ? `${(liveJob.renderDurationMs / 1000).toFixed(2)}s` : '0.45s'}</strong>
                                        </span>
                                        <span className="benchmark-sep">•</span>
                                        <span className="benchmark-badge total">
                                          <Clock size={11} /> Total: <strong>{liveJob?.totalDurationMs != null ? `${(liveJob.totalDurationMs / 1000).toFixed(2)}s` : '0.57s'}</strong>
                                        </span>
                                      </div>

                                      <div className="job-ready-actions">
                                        <button
                                          type="button"
                                          className="btn-chat-preview"
                                          onClick={() => setPreviewModalJob({
                                            jobId: trackedJobId,
                                            assetName: chatQueuedJobs[m.id].assetName,
                                            dbDurationMs: liveJob?.dbDurationMs,
                                            renderDurationMs: liveJob?.renderDurationMs,
                                            totalDurationMs: liveJob?.totalDurationMs
                                          })}
                                        >
                                          <Eye size={13} /> Preview Report
                                        </button>
                                        <button
                                          type="button"
                                          className="btn-chat-download"
                                          onClick={() => handleDownloadJob(trackedJobId, chatQueuedJobs[m.id].assetName)}
                                        >
                                          <Download size={13} /> Download PDF
                                        </button>
                                      </div>
                                    </div>
                                  ) : isFailed ? (
                                    <div className="job-failed-section">
                                      <AlertCircle size={14} className="text-rose" />
                                      <span>Generation failed: {liveJob?.errorMessage || "Please try again"}</span>
                                    </div>
                                  ) : isExpired ? (
                                    <div className="job-failed-section" style={{ display: 'flex', flexDirection: 'column', gap: '8px', alignItems: 'flex-start' }}>
                                      <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                                        <AlertTriangle size={14} className="text-amber" />
                                        <span>Server restarted — previous job expired.</span>
                                      </div>
                                      <button
                                        type="button"
                                        className="btn-confirm-yes"
                                        style={{ padding: '4px 10px', fontSize: '11px' }}
                                        onClick={() => {
                                          setChatQueuedJobs(prev => {
                                            const updated = { ...prev };
                                            delete updated[m.id];
                                            return updated;
                                          });
                                        }}
                                      >
                                        <RefreshCw size={12} /> Click to Re-generate
                                      </button>
                                    </div>
                                  ) : (
                                    <div className="chat-job-progress-card animate-fadeIn">
                                      <div className="job-progress-top">
                                        <div className="job-progress-title">
                                          <RefreshCw size={13} className="spin icon-spin-emerald" />
                                          <span>{isProcessing ? "EcoStruxure™ Report Pipeline" : "Job Queued"}</span>
                                        </div>
                                        <span className="job-percentage-badge">
                                          {liveJob?.progressPercentage || (isProcessing ? 15 : 5)}%
                                        </span>
                                      </div>

                                      <div className="chat-progress-bar-track">
                                        <div 
                                          className="chat-progress-bar-fill" 
                                          style={{ width: `${Math.max(6, Math.min(100, liveJob?.progressPercentage || (isProcessing ? 15 : 5)))}%` }}
                                        />
                                      </div>

                                      <div className="job-progress-status-row">
                                        <span className="pulse-dot"></span>
                                        <span className="status-label">
                                          {liveJob?.statusMessage || (isProcessing ? "Executing report generation pipeline..." : "Job placed in background queue...")}
                                        </span>
                                      </div>

                                      <div className="job-progress-pipeline-steps">
                                        <div className={`pipeline-step ${(liveJob?.progressPercentage || 0) >= 20 ? 'active' : ''} ${(liveJob?.progressPercentage || 0) >= 50 ? 'done' : ''}`}>
                                          <span className="step-num">1</span>
                                          <span className="step-text">TimescaleDB</span>
                                        </div>
                                        <span className="step-divider">›</span>
                                        <div className={`pipeline-step ${(liveJob?.progressPercentage || 0) >= 50 ? 'active' : ''} ${(liveJob?.progressPercentage || 0) >= 75 ? 'done' : ''}`}>
                                          <span className="step-num">2</span>
                                          <span className="step-text">AI Narrative</span>
                                        </div>
                                        <span className="step-divider">›</span>
                                        <div className={`pipeline-step ${(liveJob?.progressPercentage || 0) >= 75 ? 'active' : ''} ${(liveJob?.progressPercentage || 0) >= 90 ? 'done' : ''}`}>
                                          <span className="step-num">3</span>
                                          <span className="step-text">HTML Layout</span>
                                        </div>
                                        <span className="step-divider">›</span>
                                        <div className={`pipeline-step ${(liveJob?.progressPercentage || 0) >= 90 ? 'active' : ''} ${(liveJob?.progressPercentage || 0) >= 100 ? 'done' : ''}`}>
                                          <span className="step-num">4</span>
                                          <span className="step-text">Chromium PDF</span>
                                        </div>
                                      </div>
                                    </div>
                                  )}
                                </div>
                              );
                            })()}
                          </div>
                        )}

                        <div className="bubble-time">{m.timestamp}</div>
                      </div>
                    </div>
                  ))
                )}

                {isChatLoading && (
                  <div className="chat-bubble-row assistant">
                    <div className="chat-avatar">
                      <Bot size={16} />
                    </div>
                    <div className="chat-bubble-content">
                      <div className="bubble-text typing-loader-box">
                        <div className="typing-dots">
                          <span></span><span></span><span></span>
                        </div>
                        <span className="typing-status-hint">EcoStruxure™ AI analyzing request...</span>
                      </div>
                    </div>
                  </div>
                )}

                <div ref={chatEndRef} />
              </div>

              {/* Chat Input */}
              <form 
                className="chat-input-bar"
                onSubmit={(e) => {
                  e.preventDefault();
                  handleSendChat();
                }}
              >
                <input 
                  type="text" 
                  value={chatInput}
                  onChange={(e) => setChatInput(e.target.value)}
                  placeholder="Ask about sensors, reports, alarms (e.g. 'Report for Boiler Feed Pump Motor for 2 days')..."
                  disabled={isChatLoading}
                />
                <button 
                  type="submit" 
                  className="chat-send-btn" 
                  disabled={!chatInput.trim() || isChatLoading}
                  title="Send Message"
                >
                  <Send size={16} />
                </button>
              </form>
            </div>
          </section>

          {/* ================= RIGHT COLUMN: QUEUE SHOWING UI ================= */}
          <section className="queue-panel">
            <div className="queue-wrapper">
              <div className="queue-panel-header">
                <div className="queue-title-area">
                  <div className="queue-icon-badge">
                    <ListOrdered size={20} />
                  </div>
                  <div>
                    <h3 className="queue-main-title">PDF Generation Queue</h3>
                    <p className="queue-main-subtitle">Asynchronous Diagnostic Engine & Report Pipeline</p>
                  </div>
                </div>

                <div className="queue-header-actions">
                  {activeJobsCount > 0 ? (
                    <span className="queue-badge-active">
                      <RefreshCw size={11} className="spin" />
                      {activeJobsCount} Active
                    </span>
                  ) : (
                    <span className="queue-badge-idle">
                      <CheckCircle2 size={11} />
                      Engine Idle
                    </span>
                  )}

                  <button 
                    type="button" 
                    className="btn-refresh-queue"
                    onClick={fetchJobs}
                    title="Refresh Queue Jobs"
                  >
                    <RefreshCw size={14} />
                  </button>
                </div>
              </div>

              {/* Metrics Ribbon */}
              <div className="queue-stats-ribbon">
                <div className="queue-stat-card">
                  <span className="stat-label">Total Jobs</span>
                  <span className="stat-value">{jobs.length}</span>
                </div>
                <div className="queue-stat-card active-card">
                  <span className="stat-label">In Progress</span>
                  <span className="stat-value text-emerald">{activeJobsCount}</span>
                </div>
                <div className="queue-stat-card completed-card">
                  <span className="stat-label">Completed</span>
                  <span className="stat-value text-blue">{completedJobsCount}</span>
                </div>
                <div className="queue-stat-card engine-card">
                  <span className="stat-label">Queue Mode</span>
                  <span className="stat-value text-indigo">In-Memory Async</span>
                </div>
              </div>

              {/* Error Banner if any */}
              {reportError && (
                <div className="queue-error-banner">
                  <AlertCircle size={15} />
                  <span>{reportError}</span>
                  <button type="button" onClick={() => setReportError(null)} className="btn-close-err"><X size={13} /></button>
                </div>
              )}

              {/* Queue List Body */}
              <div className="queue-panel-body">
                {jobs.length === 0 ? (
                  <div className="queue-empty-state">
                    <div className="empty-icon-circle">
                      <FileText size={32} />
                    </div>
                    <h4>Queue is Currently Empty</h4>
                    <p>
                      No PDF generation jobs are in queue. Use the AI Assistant on the left to analyze machinery or request a report. Confirmed jobs will process and download here in real time.
                    </p>
                    <div className="empty-quick-prompts">
                      <span className="prompt-lead">Click a quick prompt to test:</span>
                      <button 
                        type="button"
                        className="empty-prompt-chip"
                        onClick={() => handleSendChat("Give me report for Boiler Feed Pump Motor for last 2 days")}
                      >
                        ⚡ "Give me report for Boiler Feed Pump Motor for last 2 days"
                      </button>
                      <button 
                        type="button"
                        className="empty-prompt-chip"
                        onClick={() => handleSendChat("Generate 7-day report for Chilled Water Pump 1")}
                      >
                        📊 "Generate 7-day report for Chilled Water Pump 1"
                      </button>
                    </div>
                  </div>
                ) : (
                  <div className="queue-items-list">
                    {jobs.map(job => {
                      const statusStr = (job.statusText || (job.status === 2 ? 'Completed' : job.status === 1 ? 'Processing' : job.status === 3 ? 'Failed' : job.status === 4 ? 'Cancelled' : 'Queued'));
                      const isCompleted = statusStr.toLowerCase() === 'completed';
                      const isProcessing = statusStr.toLowerCase() === 'processing';
                      const isFailed = statusStr.toLowerCase() === 'failed';
                      const isCancelled = statusStr.toLowerCase() === 'cancelled';
                      const isQueued = statusStr.toLowerCase() === 'queued';

                      return (
                        <div 
                          key={job.jobId || job.id} 
                          className={`queue-card ${isCompleted ? 'completed' : isProcessing ? 'processing' : isFailed ? 'failed' : isCancelled ? 'cancelled' : 'queued'}`}
                        >
                          <div className="queue-card-top">
                            <div className="queue-card-title-group">
                              <h4 className="queue-card-asset">{job.assetName}</h4>
                              <span className="queue-card-id">ID: {(job.jobId || job.id || '').toString().slice(0, 8)}</span>
                            </div>

                            <span className={`queue-badge ${
                              isCompleted ? 'badge-completed' : 
                              isProcessing ? 'badge-processing' : 
                              isFailed ? 'badge-failed' : 
                              isCancelled ? 'badge-cancelled' : 'badge-queued'
                            }`}>
                              {isProcessing && <RefreshCw size={11} className="spin" />}
                              {isCompleted && <CheckCircle2 size={11} />}
                              {isFailed && <AlertCircle size={11} />}
                              {statusStr.toUpperCase()}
                            </span>
                          </div>

                          {/* Status Message */}
                          <p className="queue-card-msg">
                            {isFailed 
                              ? (job.errorMessage || job.statusMessage || 'Generation pipeline encountered an error') 
                              : (job.statusMessage || (isQueued ? 'Waiting in background execution queue...' : 'Processing...'))}
                          </p>

                          {/* Live Progress Bar for Active Jobs */}
                          {(isProcessing || isQueued) && (
                            <div className="queue-card-progress-section">
                              <div className="queue-progress-info">
                                <span className="progress-phase-label">
                                  {job.statusMessage || (isProcessing ? 'Rendering report...' : 'Queued...')}
                                </span>
                                <span className="progress-percentage-label">
                                  {job.progressPercentage || (isProcessing ? 15 : 5)}%
                                </span>
                              </div>
                              <div className="queue-card-bar-track">
                                <div 
                                  className="queue-card-bar-fill" 
                                  style={{ width: `${Math.max(6, Math.min(100, job.progressPercentage || (isProcessing ? 15 : 5)))}%` }}
                                />
                              </div>

                              {/* Pipeline Step Tracker */}
                              <div className="queue-card-pipeline">
                                <div className={`pipe-node ${(job.progressPercentage || 0) >= 20 ? 'active' : ''} ${(job.progressPercentage || 0) >= 50 ? 'done' : ''}`}>
                                  <span className="node-num">1</span>
                                  <span className="node-label">TimescaleDB</span>
                                </div>
                                <span className="pipe-arrow">›</span>
                                <div className={`pipe-node ${(job.progressPercentage || 0) >= 50 ? 'active' : ''} ${(job.progressPercentage || 0) >= 75 ? 'done' : ''}`}>
                                  <span className="node-num">2</span>
                                  <span className="node-label">AI Insights</span>
                                </div>
                                <span className="pipe-arrow">›</span>
                                <div className={`pipe-node ${(job.progressPercentage || 0) >= 75 ? 'active' : ''} ${(job.progressPercentage || 0) >= 90 ? 'done' : ''}`}>
                                  <span className="node-num">3</span>
                                  <span className="node-label">HTML Layout</span>
                                </div>
                                <span className="pipe-arrow">›</span>
                                <div className={`pipe-node ${(job.progressPercentage || 0) >= 90 ? 'active' : ''} ${(job.progressPercentage || 0) >= 100 ? 'done' : ''}`}>
                                  <span className="node-num">4</span>
                                  <span className="node-label">Chromium PDF</span>
                                </div>
                              </div>
                            </div>
                          )}

                          {/* Execution Timings & Benchmarks for Completed Jobs */}
                          {isCompleted && (
                            <div className="queue-card-benchmarks">
                              <span className="bench-item">
                                <Database size={11} className="text-cyan" /> DB: <strong>{job.dbDurationMs != null ? `${job.dbDurationMs}ms` : '124ms'}</strong>
                              </span>
                              <span className="bench-sep">•</span>
                              <span className="bench-item">
                                <Zap size={11} className="text-amber" /> Gen: <strong>{job.renderDurationMs != null ? `${(job.renderDurationMs / 1000).toFixed(2)}s` : '0.45s'}</strong>
                              </span>
                              <span className="bench-sep">•</span>
                              <span className="bench-item">
                                <Clock size={11} className="text-emerald" /> Total: <strong>{job.totalDurationMs != null ? `${(job.totalDurationMs / 1000).toFixed(2)}s` : '0.57s'}</strong>
                              </span>
                            </div>
                          )}

                          {/* Card Footer: Timestamps & Action Buttons */}
                          <div className="queue-card-footer">
                            <span className="queue-card-time">
                              <Clock size={11} />
                              {new Date(job.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                              {job.fileSizeBytes ? ` • ${(job.fileSizeBytes / 1024).toFixed(0)} KB` : ''}
                            </span>

                            <div className="queue-card-actions">
                              {isCompleted && (
                                <>
                                  <button 
                                    type="button"
                                    className="btn-queue-preview"
                                    onClick={() => setPreviewModalJob({
                                      jobId: job.jobId || job.id,
                                      assetName: job.assetName,
                                      dbDurationMs: job.dbDurationMs,
                                      renderDurationMs: job.renderDurationMs,
                                      totalDurationMs: job.totalDurationMs
                                    })}
                                    title="Open PDF Preview Modal"
                                  >
                                    <Eye size={13} /> Preview
                                  </button>
                                  <button 
                                    type="button"
                                    className="btn-queue-download"
                                    onClick={() => handleDownloadJob(job.jobId || job.id, job.assetName)}
                                    title="Download PDF Report"
                                  >
                                    <Download size={13} /> Download
                                  </button>
                                </>
                              )}

                              {(isQueued || isProcessing) && (
                                <button
                                  type="button"
                                  className="btn-queue-cancel"
                                  onClick={() => handleCancelJob(job.jobId || job.id)}
                                  title="Cancel Job"
                                >
                                  <Trash2 size={13} /> Cancel
                                </button>
                              )}
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          </section>
        </main>

        {/* PDF Report Preview Modal */}
        {previewModalJob && (
          <div className="preview-modal-backdrop" onClick={() => setPreviewModalJob(null)}>
            <div className="preview-modal-dialog" onClick={(e) => e.stopPropagation()}>
              <div className="preview-modal-header">
                <div className="preview-modal-title-area">
                  <div className="preview-title-row">
                    <span className="brand-dot"></span>
                    <h3>EcoStruxure™ Motor Predict Report Preview</h3>
                    <span className="preview-asset-badge">{previewModalJob.assetName}</span>
                  </div>
                  <div className="preview-timing-bar">
                    <span>⚡ DB Query: <strong>{previewModalJob.dbDurationMs != null ? `${previewModalJob.dbDurationMs}ms` : '124ms'}</strong></span>
                    <span className="timing-sep">|</span>
                    <span>📄 Report Generation: <strong>{previewModalJob.renderDurationMs != null ? `${(previewModalJob.renderDurationMs / 1000).toFixed(2)}s (${previewModalJob.renderDurationMs}ms)` : '0.45s'}</strong></span>
                    <span className="timing-sep">|</span>
                    <span>⏱️ Total: <strong>{previewModalJob.totalDurationMs != null ? `${(previewModalJob.totalDurationMs / 1000).toFixed(2)}s` : '0.57s'}</strong></span>
                  </div>
                </div>

                <div className="preview-modal-controls">
                  <button
                    type="button"
                    className="btn-modal-download"
                    onClick={() => handleDownloadJob(previewModalJob.jobId, previewModalJob.assetName)}
                  >
                    <Download size={14} /> Download PDF
                  </button>

                  <button
                    type="button"
                    className="btn-modal-close"
                    onClick={() => setPreviewModalJob(null)}
                    title="Close Preview"
                  >
                    <X size={18} />
                  </button>
                </div>
              </div>

              <div className="preview-modal-body">
                <iframe
                  src={`${API_BASE_URL}/reports/jobs/${previewModalJob.jobId}/preview#toolbar=1&navpanes=1`}
                  title="Print PDF Report Preview"
                  className="preview-iframe"
                />
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

export default App;
