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
  Activity, 
  Sliders, 
  TrendingUp, 
  AlertTriangle, 
  Clock, 
  CheckSquare, 
  Square,
  Sparkles,
  BarChart2,
  Calendar,
  Layers,
  ChevronRight,
  Database,
  ListOrdered,
  Server,
  Zap,
  Wifi
} from 'lucide-react';
import './App.css';

const SIGNAL_COLORS = ['#009E4D', '#007A3C', '#005A2C', '#0084D6', '#F5A623', '#D93025', '#10B981', '#059669'];
const API_BASE_URL = 'http://localhost:5000/api';

function App() {
  // Report State
  const [assets, setAssets] = useState([]);
  const [selectedAssetId, setSelectedAssetId] = useState('');
  const [signals, setSignals] = useState([]);
  const [selectedSignalIds, setSelectedSignalIds] = useState([]);
  const [timeRange, setTimeRange] = useState('7d');
  const [includeEvents, setIncludeEvents] = useState(true);
  const [includeAlerts, setIncludeAlerts] = useState(true);
  const [includeInsights, setIncludeInsights] = useState(true);
  const [includeCharts, setIncludeCharts] = useState(true);
  const [includeFullRawData, setIncludeFullRawData] = useState(true);

  const [reportData, setReportData] = useState(null);
  const [isGenerating, setIsGenerating] = useState(false);
  const [reportError, setReportError] = useState(null);
  const [jobs, setJobs] = useState([]);
  const [isQueueing, setIsQueueing] = useState(false);

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

  const chatEndRef = useRef(null);
  const hubConnectionRef = useRef(null);

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
      .withUrl(`http://localhost:5000/hubs/chat`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets
      })
      .withAutomaticReconnect()
      .build();

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

    connection.start()
      .then(() => {
        setWsConnected(true);
        connection.invoke("GetHistory", sessionId);
      })
      .catch(err => {
        console.warn("SignalR Connection failed, falling back to REST API", err);
        setWsConnected(false);
      });

    hubConnectionRef.current = connection;

    return () => {
      connection.stop();
    };
  }, [sessionId]);

  // Fetch signals when asset changes
  useEffect(() => {
    if (selectedAssetId) {
      fetchSignals(selectedAssetId);
    } else {
      setSignals([]);
      setSelectedSignalIds([]);
    }
  }, [selectedAssetId]);

  useEffect(() => {
    chatEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages, isChatLoading]);

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

  const fetchSignals = async (assetId) => {
    try {
      const res = await fetch(`${API_BASE_URL}/reports/assets/${assetId}/signals`);
      if (res.ok) {
        const data = await res.json();
        setSignals(data);
        // Default select first 6 critical signals
        const defaultSelected = data.slice(0, 6).map(s => s.signalId);
        setSelectedSignalIds(defaultSelected);
      }
    } catch (err) {
      console.error('Failed to load signals', err);
    }
  };

  const toggleSignal = (signalId) => {
    setSelectedSignalIds(prev => 
      prev.includes(signalId) ? prev.filter(id => id !== signalId) : [...prev, signalId]
    );
  };

  const selectAllSignals = () => {
    setSelectedSignalIds(signals.map(s => s.signalId));
  };

  const clearSignals = () => {
    setSelectedSignalIds([]);
  };

  /**
   * Compute from/to dates from a timeRange string (e.g. "5.5d", "24h", "2w").
   * Always called with the actual value to use — never relies on potentially-stale React state.
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
        // Hours: "24h", "12h", "1.5h"
        from.setTime(to.getTime() - Math.round(num * 3600 * 1000));
      } else if (str.includes('w') || str.includes('week')) {
        // Weeks: "1w", "2.5w", "2 weeks"
        from.setTime(to.getTime() - Math.round(num * 7 * 86400 * 1000));
      } else if (str === `${num}m` || str.includes('min')) {
        // Minutes: "30m", "45min" — rare but guard against LLM emitting it
        from.setTime(to.getTime() - Math.round(num * 60 * 1000));
      } else {
        // Days: "5d", "5.5d", "5 days", "20", bare numbers default to days
        from.setTime(to.getTime() - Math.round(num * 86400 * 1000));
      }
    } else {
      from.setDate(to.getDate() - 7);
    }

    return { from: from.toISOString(), to: to.toISOString() };
  };

  // Convenience wrapper that reads from React state (for the sidebar controls)
  const getTimeRangeDates = () => computeDateRange(timeRange);

  const handleGeneratePreview = async () => {
    if (!selectedAssetId) return;

    setIsGenerating(true);
    setReportError(null);

    const { from, to } = getTimeRangeDates();

    try {
      const res = await fetch(`${API_BASE_URL}/reports/preview`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetId: parseInt(selectedAssetId),
          signalIds: selectedSignalIds.length > 0 ? selectedSignalIds : null,
          from,
          to,
          includeEvents,
          includeAlerts,
          includeInsights,
          includeCharts
        })
      });

      if (!res.ok) throw new Error(`Server returned HTTP ${res.status}`);
      const data = await res.json();
      setReportData(data);
    } catch (err) {
      console.error(err);
      setReportError(`Failed to generate report: ${err.message}`);
    } finally {
      setIsGenerating(false);
    }
  };

  // Background PDF Queue Handlers
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

  useEffect(() => {
    fetchJobs();
  }, []);

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

  const handleQueuePdf = async () => {
    if (!selectedAssetId) return;
    // Read state at call time — safe because this is triggered from the sidebar controls
    // where selectedAssetId and timeRange are already up-to-date.
    await _queuePdfJob(parseInt(selectedAssetId), timeRange);
  };

  /**
   * Core queue function. Accepts explicit assetId + rangeStr or absolute fromDate/toDate.
   * Called from chat bubble with exact LLM-extracted values — no stale React state dependency.
   */
  const _queuePdfJob = async (assetId, rangeStr, fromDateIso = null, toDateIso = null) => {
    if (!assetId) return;

    setIsQueueing(true);
    setReportError(null);

    // Prefer absolute dates from LLM; fall back to relative range computation
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
          signalIds: selectedSignalIds.length > 0 ? selectedSignalIds : null,
          from,
          to,
          includeEvents,
          includeAlerts,
          includeInsights,
          includeCharts,
          includeFullRawData
        })
      });

      if (!res.ok) throw new Error(`Server returned HTTP ${res.status}`);
      const data = await res.json();
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

  // Chat Handlers — WebSockets only (SignalR)
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

  // Format chart time-series data for Recharts
  const formatChartData = () => {
    if (!reportData || !reportData.signals || reportData.signals.length === 0) return [];

    const signalsWithData = reportData.signals.filter(s => s.dataPoints && s.dataPoints.length > 0);
    if (signalsWithData.length === 0) return [];

    // Use timestamps of first signal as base
    const basePoints = signalsWithData[0].dataPoints;
    return basePoints.map((pt, idx) => {
      const row = {
        time: new Date(pt.time).toLocaleDateString([], { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
      };
      signalsWithData.slice(0, 5).forEach(sig => {
        if (sig.dataPoints[idx]) {
          row[sig.name] = sig.dataPoints[idx].value;
        }
      });
      return row;
    });
  };

  const chartData = formatChartData();
  const activeSignalsForChart = reportData?.signals?.filter(s => s.dataPoints?.length > 0).slice(0, 5) || [];

  return (
    <div className="layout-root">
      {/* ALWAYS OPEN LEFT SIDEBAR */}
      <aside className="app-sidebar">
        <div className="sidebar-header">
          <div className="sidebar-logo">
            <Activity size={22} />
          </div>
          <div className="sidebar-brand-info">
            <h2 className="sidebar-title">EcoStruxure™</h2>
            <span className="sidebar-subtitle">Asset Advisor • PDF Engine</span>
          </div>
        </div>

        {/* PDF Generation Queue Container */}
        <div className="sidebar-section queue-section">
          <div className="sidebar-section-title queue-header">
            <div className="title-left">
              <ListOrdered size={15} className="icon-indigo" />
              <span>PDF Job Queue</span>
              {jobs.filter(j => {
                const s = (j.statusText || j.status || '').toString().toLowerCase();
                return s === 'queued' || s === '0' || s === 'processing' || s === '1';
              }).length > 0 && (
                <span className="queue-active-count">
                  <RefreshCw size={9} className="spin" />
                  {jobs.filter(j => {
                    const s = (j.statusText || j.status || '').toString().toLowerCase();
                    return s === 'queued' || s === '0' || s === 'processing' || s === '1';
                  }).length} Active
                </span>
              )}
            </div>
            <button 
              type="button" 
              className="btn-refresh-sm"
              onClick={fetchJobs}
              title="Refresh background job status"
            >
              <RefreshCw size={12} />
            </button>
          </div>

          <div className="sidebar-queue-body">
            {jobs.length === 0 ? (
              <div className="sidebar-queue-empty">
                <Clock size={24} className="empty-icon-muted" />
                <p>Queue is empty</p>
                <span>Click "Queue Full PDF" to generate unaggregated report asynchronously</span>
              </div>
            ) : (
              <div className="sidebar-queue-list">
                {jobs.map(job => {
                  const statusStr = (job.statusText || (job.status === 2 ? 'Completed' : job.status === 1 ? 'Processing' : job.status === 3 ? 'Failed' : job.status === 4 ? 'Cancelled' : 'Queued'));
                  const isCompleted = statusStr.toLowerCase() === 'completed';
                  const isProcessing = statusStr.toLowerCase() === 'processing';
                  const isFailed = statusStr.toLowerCase() === 'failed';
                  const isCancelled = statusStr.toLowerCase() === 'cancelled';
                  const isQueued = statusStr.toLowerCase() === 'queued';

                  return (
                    <div 
                      key={job.jobId} 
                      className={`sidebar-queue-item ${isCompleted ? 'completed' : isProcessing ? 'processing' : isFailed ? 'failed' : isCancelled ? 'cancelled' : ''}`}
                    >
                      <div className="queue-item-header">
                        <span className="queue-item-name">{job.assetName}</span>
                        <span className={`badge-sm ${
                          isCompleted ? 'badge-success' : 
                          isProcessing ? 'badge-processing' : 
                          isFailed ? 'badge-danger' : 
                          isCancelled ? 'badge-muted' : 'badge-warning'
                        }`}>
                          {isProcessing && <RefreshCw size={9} className="spin" />}
                          {isCompleted && <CheckCircle2 size={9} />}
                          {statusStr.toUpperCase()}
                        </span>
                      </div>

                      <div className="queue-item-msg">
                        {isFailed ? (job.errorMessage || job.statusMessage || 'Failed to generate report') : (job.statusMessage || (isQueued ? 'Queued in Memory...' : ''))}
                      </div>

                      {(isProcessing || isQueued) && (
                        <div className="queue-progress-bar">
                          <div 
                            className="queue-progress-fill" 
                            style={{ width: `${Math.max(8, job.progressPercentage || 10)}%` }}
                          />
                        </div>
                      )}

                      <div className="queue-item-footer">
                        <span className="queue-time">
                          {new Date(job.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                          {job.fileSizeBytes ? ` • ${(job.fileSizeBytes / 1024).toFixed(0)}KB` : ''}
                        </span>

                        {isCompleted ? (
                          <button 
                            className="btn-download-icon"
                            onClick={() => handleDownloadJob(job.jobId, job.assetName)}
                            title="Download Completed PDF"
                          >
                            <Download size={12} /> Download PDF
                          </button>
                        ) : (isQueued || isProcessing) ? (
                          <button
                            type="button"
                            className="btn-cancel-icon"
                            onClick={() => handleCancelJob(job.jobId)}
                            title="Cancel Job"
                          >
                            <Trash2 size={12} /> Cancel
                          </button>
                        ) : null}
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      </aside>

      {/* MAIN WORKSPACE */}
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

        {/* Main Split Screen Container */}
        <div className="split-view">
          {/* ================= LEFT COLUMN: REPORT GENERATION UI ================= */}
          <section className="left-panel">
            {/* Controls Bar */}
            <div className="panel-card config-card">
              <div className="card-header">
                <div className="card-title">
                  <Sliders size={18} />
                  <span>Report Parameters</span>
                </div>
                <div className="action-buttons" style={{ width: '100%' }}>
                  <button 
                    className="btn btn-primary" 
                    onClick={handleQueuePdf}
                    disabled={isQueueing || !selectedAssetId}
                    title={includeFullRawData ? "Queue report with all unaggregated data rows" : "Queue report with graphs & trend curves only"}
                    style={{ width: '100%', justifyContent: 'center', padding: '0.75rem 1rem', fontSize: '0.925rem' }}
                  >
                    <ListOrdered size={16} />
                    {isQueueing ? "Enqueuing PDF Job..." : (includeFullRawData ? "Queue Full PDF Report (All Rows)" : "Queue Graphs & Trends PDF Report")}
                  </button>
                </div>
              </div>

              {/* REPORT CONTENT MODE TOGGLE SWITCH */}
              <div className="report-mode-toggle-card">
                <div className="mode-info">
                  <div className="mode-title-row">
                    {includeFullRawData ? <Database size={16} className="mode-icon text-indigo" /> : <TrendingUp size={16} className="mode-icon text-amber" />}
                    <span className="mode-title">Report Content: <strong>{includeFullRawData ? "Full Data Table (All Rows)" : "Graphs & Trends Only"}</strong></span>
                  </div>
                  <div className="mode-subtitle">
                    {includeFullRawData 
                      ? "PDF will include summary metrics plus complete chronological raw data rows on subsequent pages." 
                      : "PDF will include summary metrics & enlarged trend curves, skipping raw data table rows."}
                  </div>
                </div>
                <label className="switch-control">
                  <input 
                    type="checkbox" 
                    checked={includeFullRawData} 
                    onChange={(e) => setIncludeFullRawData(e.target.checked)} 
                  />
                  <span className="slider-round"></span>
                </label>
              </div>

              <div className="form-grid">
                {/* Asset Dropdown */}
                <div className="form-group">
                  <label className="form-label">
                    <Layers size={14} /> Target Asset
                  </label>
                  <select 
                    className="form-select"
                    value={selectedAssetId}
                    onChange={(e) => setSelectedAssetId(e.target.value)}
                  >
                    {assets.map(a => (
                      <option key={a.assetId} value={a.assetId}>
                        {a.name} ({a.location || a.assetType})
                      </option>
                    ))}
                  </select>
                </div>

                {/* Time Range */}
                <div className="form-group">
                  <label className="form-label">
                    <Calendar size={14} /> Telemetry Timeframe
                  </label>
                  <div className="pill-group">
                    {['24h', '7d', '14d', '30d'].map(range => (
                      <button
                        key={range}
                        type="button"
                        className={`pill-btn ${timeRange === range ? 'active' : ''}`}
                        onClick={() => setTimeRange(range)}
                      >
                        {range.toUpperCase()}
                      </button>
                    ))}
                  </div>
                </div>
              </div>

              {/* Checkbox Options */}
              <div className="toggles-row">
                <label className="checkbox-label">
                  <input 
                    type="checkbox" 
                    checked={includeEvents} 
                    onChange={(e) => setIncludeEvents(e.target.checked)} 
                  />
                  <span>Include Excursion Events</span>
                </label>

                <label className="checkbox-label">
                  <input 
                    type="checkbox" 
                    checked={includeAlerts} 
                    onChange={(e) => setIncludeAlerts(e.target.checked)} 
                  />
                  <span>Include System Alerts</span>
                </label>

                <label className="checkbox-label">
                  <input 
                    type="checkbox" 
                    checked={includeCharts} 
                    onChange={(e) => setIncludeCharts(e.target.checked)} 
                  />
                  <span>Render Trend Charts</span>
                </label>

                <label className="checkbox-label">
                  <input 
                    type="checkbox" 
                    checked={includeInsights} 
                    onChange={(e) => setIncludeInsights(e.target.checked)} 
                  />
                  <span>AI / Engineering Insights</span>
                </label>
              </div>

              {/* Signals Selection Pills */}
              <div className="signals-section">
                <div className="signals-header">
                  <span className="form-label">
                    Signals ({selectedSignalIds.length} of {signals.length} selected)
                  </span>
                  <div className="quick-actions">
                    <button type="button" onClick={selectAllSignals}>Select All</button>
                    <span>•</span>
                    <button type="button" onClick={clearSignals}>Clear</button>
                  </div>
                </div>

                <div className="signals-scroll-box">
                  {signals.map(s => {
                    const isChecked = selectedSignalIds.includes(s.signalId);
                    return (
                      <div 
                        key={s.signalId} 
                        className={`signal-chip ${isChecked ? 'selected' : ''}`}
                        onClick={() => toggleSignal(s.signalId)}
                      >
                        {isChecked ? <CheckSquare size={14} /> : <Square size={14} />}
                        <span className="sig-name">{s.name}</span>
                        {s.unit && <span className="sig-unit">{s.unit}</span>}
                      </div>
                    );
                  })}
                </div>
              </div>

              {reportError && (
                <div className="error-banner">
                  <AlertCircle size={16} />
                  <span>{reportError}</span>
                </div>
              )}
            </div>
          </section>

        {/* ================= RIGHT COLUMN: CHATBOT UI ================= */}
        <aside className="right-panel">
          <div className="chat-wrapper">
            <div className="chat-header">
              <div className="chat-header-title">
                <Bot size={20} className="icon-emerald" />
                <div>
                  <h3>EcoStruxure™ AI Diagnostic Assistant</h3>
                  <p>Rotating Machine Health Advisor</p>
                </div>
              </div>

              <button 
                className="icon-btn-sm" 
                onClick={handleClearChat} 
                title="Clear Chat History"
              >
                <Trash2 size={16} />
              </button>
            </div>

            {/* Chat Messages */}
            <div className="chat-messages">
              {messages.length === 0 ? (
                <div className="chat-empty">
                  <div className="chat-bot-icon">
                    <Bot size={28} />
                  </div>
                  <h4>How can I help with your reports?</h4>
                  <p>Ask about machine telemetry, temperatures, pump vibrations, or generate insights.</p>

                  <div className="chat-suggestions">
                    <button 
                      onClick={() => handleSendChat("Give me report for Boiler Feed Pump Motor for last 2 days")}
                      className="chat-chip on-topic"
                    >
                      <ChevronRight size={13} /> "Give me report for Boiler Feed Pump Motor for last 2 days"
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
                            {(m.isComplete || m.suggestedAction === 'confirm_queue' || (m.extractedParameters?.assetName && (m.extractedParameters?.timeRange || m.extractedParameters?.fromDate))) && (
                              <span className="complete-tag"><CheckCircle2 size={10} /> READY</span>
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
                                From: <strong>{new Date(m.extractedParameters.fromDate).toLocaleDateString()}</strong>
                              </div>
                            )}
                            {m.extractedParameters.toDate && (
                              <div className="param-chip">
                                To: <strong>{new Date(m.extractedParameters.toDate).toLocaleDateString()}</strong>
                              </div>
                            )}
                            {m.extractedParameters.mode && (
                              <div className="param-chip">Format: <strong>{m.extractedParameters.mode === 'raw' ? 'Full Rows' : 'Graphs Only'}</strong></div>
                            )}
                          </div>

                          {/* Confirmation Buttons: Yes, Queue or Change Options */}
                          {(m.isComplete || m.suggestedAction === 'confirm_queue' || (m.extractedParameters?.assetName && (m.extractedParameters?.timeRange || m.extractedParameters?.fromDate))) && !chatQueuedJobs[m.id] && (
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
                                <CheckCircle2 size={13} /> {isQueueing ? "Queuing..." : "Yes"}
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
                                    <button
                                      type="button"
                                      className="btn-chat-download"
                                      onClick={() => handleDownloadJob(trackedJobId, chatQueuedJobs[m.id].assetName)}
                                    >
                                      <Download size={14} /> Download PDF Report
                                    </button>
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
                                  <div className="job-processing-section">
                                    <RefreshCw size={14} className="spin text-indigo" />
                                    <span>{isProcessing ? "Rendering Schneider Electric EcoStruxure PDF report..." : "Job placed in background queue..."}</span>
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
                    <div className="bubble-text typing">
                      <span></span><span></span><span></span>
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
                placeholder="Ask about sensors, reports, alarms..."
                disabled={isChatLoading}
              />
              <button 
                type="submit" 
                className="chat-send-btn" 
                disabled={!chatInput.trim() || isChatLoading}
              >
                <Send size={16} />
              </button>
            </form>
          </div>
        </aside>
      </div>
    </div>
  </div>
);
}

export default App;
