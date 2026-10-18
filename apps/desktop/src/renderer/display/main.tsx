import React from 'react';
import { createRoot } from 'react-dom/client';
import { DisplayApp } from './DisplayApp';
import '../styles.css';

const el = document.getElementById('root')!;
createRoot(el).render(
  <React.StrictMode>
    <DisplayApp />
  </React.StrictMode>,
);
