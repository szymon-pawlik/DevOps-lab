import { useState, useEffect } from 'react';
import axios from 'axios';
import './App.scss';

interface Job {
  id: string;
  text: string;
  createdAt: string;
  status: 'pending' | 'processing' | 'completed';
}

// API URL - in browser always use localhost (ports are mapped in docker-compose)
const API_URL = import.meta.env.VITE_API_URL || 'http://localhost:8080';

function App() {
  const [text, setText] = useState('');
  const [jobs, setJobs] = useState<Job[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [apiStatus, setApiStatus] = useState<'checking' | 'online' | 'offline'>('checking');

  const checkApiStatus = async () => {
    try {
      const response = await axios.get(`${API_URL}/api/job/health`);
      setApiStatus(response.status === 200 ? 'online' : 'offline');
    } catch {
      setApiStatus('offline');
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;

    setLoading(true);
    setError(null);

    try {
      const response = await axios.post(`${API_URL}/api/job`, {
        text: text.trim()
      });

      const newJob: Job = {
        id: response.data.jobId,
        text: text.trim(),
        createdAt: new Date().toISOString(),
        status: 'pending'
      };

      setJobs(prev => [newJob, ...prev]);
      setText('');

      // Simulate job processing status updates
      setTimeout(() => {
        setJobs(prev => prev.map(job => 
          job.id === newJob.id ? { ...job, status: 'processing' } : job
        ));
      }, 1000);

      setTimeout(() => {
        setJobs(prev => prev.map(job => 
          job.id === newJob.id ? { ...job, status: 'completed' } : job
        ));
      }, 3000);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to submit job');
    } finally {
      setLoading(false);
    }
  };

  // Check API status on mount
  useEffect(() => {
    checkApiStatus();
    const interval = setInterval(checkApiStatus, 30000); // Check every 30s
    return () => clearInterval(interval);
  }, []);

  return (
    <div className="app">
      <header className="header">
        <h1>Producer-Consumer System</h1>
        <div className={`status ${apiStatus}`}>
          <span className="status-dot"></span>
          <span>API: {apiStatus === 'online' ? 'Online' : apiStatus === 'offline' ? 'Offline' : 'Checking...'}</span>
        </div>
      </header>

      <main className="main">
        <section className="submit-section">
          <h2>Submit New Job</h2>
          <form onSubmit={handleSubmit} className="job-form">
            <div className="form-group">
              <label htmlFor="job-text">Text to Process:</label>
              <textarea
                id="job-text"
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="Enter text to process (will be converted to uppercase)..."
                rows={4}
                disabled={loading || apiStatus === 'offline'}
              />
            </div>
            <button 
              type="submit" 
              disabled={loading || !text.trim() || apiStatus === 'offline'}
              className="submit-btn"
            >
              {loading ? 'Submitting...' : 'Submit Job'}
            </button>
          </form>
          {error && <div className="error-message">{error}</div>}
        </section>

        <section className="jobs-section">
          <h2>Job History</h2>
          {jobs.length === 0 ? (
            <div className="empty-state">No jobs submitted yet</div>
          ) : (
            <div className="jobs-list">
              {jobs.map((job) => (
                <div key={job.id} className={`job-card ${job.status}`}>
                  <div className="job-header">
                    <span className="job-id">{job.id.substring(0, 8)}...</span>
                    <span className={`job-status ${job.status}`}>
                      {job.status}
                    </span>
                  </div>
                  <div className="job-content">
                    <div className="job-text">
                      <strong>Original:</strong> {job.text}
                    </div>
                    {job.status === 'completed' && (
                      <div className="job-result">
                        <strong>Processed:</strong> {job.text.toUpperCase()}
                      </div>
                    )}
                    <div className="job-time">
                      {new Date(job.createdAt).toLocaleString()}
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </section>
      </main>
    </div>
  );
}

export default App;
